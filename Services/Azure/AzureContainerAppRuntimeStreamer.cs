using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Logging;
using Services.LogStreaming;

namespace Services.Azure;

/// <summary>
///     Polls Azure Container Apps runtime state and metrics and publishes updates through SignalR.
/// </summary>
public class AzureContainerAppRuntimeStreamer(
    ILogStreamer logStreamer,
    ILogger<AzureContainerAppRuntimeStreamer> logger) : IAzureContainerAppRuntimeStreamer
{
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveStreams = new();
    private static readonly HttpClient HttpClient = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public void StartStreaming(AzureCloudCredentialsDto credentials, DeploymentConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(credentials.AccessToken) ||
            string.IsNullOrWhiteSpace(credentials.SubscriptionId) ||
            string.IsNullOrWhiteSpace(config.CloudResourceGroupName) ||
            string.IsNullOrWhiteSpace(config.CloudContainerAppName))
            return;

        var cts = new CancellationTokenSource();
        ActiveStreams.AddOrUpdate(config.ProjectId, cts, (_, oldCts) =>
        {
            oldCts.Cancel();
            oldCts.Dispose();
            return cts;
        });

        var projectId = config.ProjectId;
        var resourceId = BuildContainerAppResourceId(credentials.SubscriptionId, config.CloudResourceGroupName,
            config.CloudContainerAppName);
        var accessToken = credentials.AccessToken;

        _ = Task.Run(async () =>
        {
            await StreamRuntimeAsync(projectId, resourceId, accessToken, cts.Token);
        }, cts.Token);
    }

    private async Task StreamRuntimeAsync(Guid projectId, string resourceId, string accessToken,
        CancellationToken cancellationToken)
    {
        var lastRevision = string.Empty;
        var lastFqdn = string.Empty;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = await GetContainerAppStateAsync(resourceId, accessToken, cancellationToken);
                if (state != null && (state.LatestRevision != lastRevision || state.Fqdn != lastFqdn))
                {
                    lastRevision = state.LatestRevision;
                    lastFqdn = state.Fqdn;

                    await logStreamer.StreamContainerLogsAsync(projectId, "cloud-web",
                        $"Azure Container App is available{(string.IsNullOrWhiteSpace(state.Fqdn) ? string.Empty : $" at https://{state.Fqdn}")}. Latest ready revision: {state.LatestRevision}.");
                }

                var metrics = await GetContainerAppMetricsAsync(resourceId, accessToken, cancellationToken);
                if (metrics != null)
                    await logStreamer.StreamContainerMetricsAsync(projectId, "cloud-web", metrics.Cpu, metrics.Memory);

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[AzureContainerAppRuntimeStreamer] Runtime streaming cancelled for Project ID {Id}.",
                projectId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[AzureContainerAppRuntimeStreamer] Error while streaming Azure Container Apps runtime data for Project ID {Id}.",
                projectId);
        }
    }

    private static async Task<ContainerAppState?> GetContainerAppStateAsync(string resourceId, string accessToken,
        CancellationToken cancellationToken)
    {
        var requestUri = $"https://management.azure.com{resourceId}?api-version=2024-03-01";
        using var document = await SendAzureRequestAsync(requestUri, accessToken, cancellationToken);
        if (document == null)
            return null;

        var properties = document.RootElement.GetProperty("properties");
        var latestRevision = GetString(properties, "latestReadyRevisionName");
        var fqdn = string.Empty;

        if (properties.TryGetProperty("configuration", out var configuration) &&
            configuration.TryGetProperty("ingress", out var ingress))
            fqdn = GetString(ingress, "fqdn");

        return new ContainerAppState(latestRevision, fqdn);
    }

    private static async Task<ContainerAppMetrics?> GetContainerAppMetricsAsync(string resourceId, string accessToken,
        CancellationToken cancellationToken)
    {
        var endTime = DateTimeOffset.UtcNow;
        var startTime = endTime.AddMinutes(-5);
        var requestUri = "https://management.azure.com" + resourceId +
                         "/providers/microsoft.insights/metrics?api-version=2023-10-01" +
                         "&metricnames=CpuUsage,MemoryWorkingSet" +
                         $"&timespan={Uri.EscapeDataString($"{startTime:O}/{endTime:O}")}&interval=PT1M&aggregation=Average";

        using var document = await SendAzureRequestAsync(requestUri, accessToken, cancellationToken);
        if (document == null)
            return null;

        var cpu = "n/a";
        var memory = "n/a";

        if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var metric in values.EnumerateArray())
        {
            var name = metric.GetProperty("name").GetProperty("value").GetString();
            var latestAverage = GetLatestAverage(metric);
            if (latestAverage == null)
                continue;

            if (string.Equals(name, "CpuUsage", StringComparison.OrdinalIgnoreCase))
                cpu = $"{latestAverage.Value:0.##} cores";
            else if (string.Equals(name, "MemoryWorkingSet", StringComparison.OrdinalIgnoreCase))
                memory = FormatBytes(latestAverage.Value);
        }

        return new ContainerAppMetrics(cpu, memory);
    }

    private static async Task<JsonDocument?> SendAzureRequestAsync(string requestUri, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
    }

    private static double? GetLatestAverage(JsonElement metric)
    {
        if (!metric.TryGetProperty("timeseries", out var timeSeries) || timeSeries.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var series in timeSeries.EnumerateArray())
        {
            if (!series.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var point in data.EnumerateArray().Reverse())
                if (point.TryGetProperty("average", out var average) && average.TryGetDouble(out var value))
                    return value;
        }

        return null;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty;
    }

    private static string FormatBytes(double bytes)
    {
        const double mebibyte = 1024 * 1024;
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / mebibyte:0.##} MiB");
    }

    private static string BuildContainerAppResourceId(string subscriptionId, string resourceGroupName,
        string containerAppName)
    {
        return $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.App/containerApps/{containerAppName}";
    }

    private sealed record ContainerAppState(string LatestRevision, string Fqdn);

    private sealed record ContainerAppMetrics(string Cpu, string Memory);
}