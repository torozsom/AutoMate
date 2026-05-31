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
    IHttpClientFactory httpClientFactory,
    ILogger<AzureContainerAppRuntimeStreamer> logger) : IAzureContainerAppRuntimeStreamer
{
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveStreams = new();
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
            return cts;
        });

        var projectId = config.ProjectId;
        var resourceId = BuildContainerAppResourceId(credentials.SubscriptionId, config.CloudResourceGroupName,
            config.CloudContainerAppName);
        var accessToken = credentials.AccessToken;

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamRuntimeAsync(projectId, resourceId, accessToken, cts.Token);
            }
            finally
            {
                if (ActiveStreams.TryGetValue(projectId, out var activeCts) && ReferenceEquals(activeCts, cts))
                    ActiveStreams.TryRemove(projectId, out _);

                cts.Dispose();
            }
        }, cts.Token);
    }


    /// <summary>
    ///     Continuously polls the Azure Container App for its latest revision, FQDN, and resource metrics,
    ///     and streams updates to clients via SignalR.
    /// </summary>
    /// <param name="projectId">The ID of the project associated with the container app.</param>
    /// <param name="resourceId">The Azure resource ID of the container app.</param>
    /// <param name="accessToken">The access token for authenticating with the Azure API.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
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


    /// <summary>
    ///     Retrieves the current state of the Azure Container App, including the latest ready revision and FQDN.
    /// </summary>
    /// <param name="resourceId">The Azure resource ID of the container app.</param>
    /// <param name="accessToken">The access token for authenticating with the Azure API.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The current state of the container app, or null if an error occurred.</returns>
    private async Task<ContainerAppState?> GetContainerAppStateAsync(string resourceId, string accessToken,
        CancellationToken cancellationToken)
    {
        var requestUri = $"https://management.azure.com{resourceId}?api-version=2024-03-01";
        using var document = await SendAzureRequestAsync(requestUri, accessToken, cancellationToken);
        if (document == null)
            return null;

        if (!document.RootElement.TryGetProperty("properties", out var properties))
            return null;

        var latestRevision = GetString(properties, "latestReadyRevisionName");
        var fqdn = string.Empty;

        if (properties.TryGetProperty("configuration", out var configuration) &&
            configuration.TryGetProperty("ingress", out var ingress))
            fqdn = GetString(ingress, "fqdn");

        return new ContainerAppState(latestRevision, fqdn);
    }


    /// <summary>
    ///     Retrieves the current resource metrics for the Azure Container App.
    /// </summary>
    /// <param name="resourceId">The Azure resource ID of the container app.</param>
    /// <param name="accessToken">The access token for authenticating with the Azure API.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The current metrics of the container app, or null if an error occurred.</returns>
    private async Task<ContainerAppMetrics?> GetContainerAppMetricsAsync(string resourceId, string accessToken,
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


    /// <summary>
    ///     Sends a GET request to the Azure Management API and parses the JSON response.
    /// </summary>
    /// <param name="requestUri">The URI of the API endpoint.</param>
    /// <param name="accessToken">The access token for authenticating with the Azure API.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The parsed JSON document, or null if an error occurred.</returns>
    private async Task<JsonDocument?> SendAzureRequestAsync(string requestUri, string accessToken,
        CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
    }


    /// <summary>
    ///     Extracts the latest average value from the metric timeseries data.
    /// </summary>
    /// <param name="metric">The metric element containing timeseries data.</param>
    /// <returns>The latest average value, or null if not found.</returns>
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


    /// <summary>
    ///     Safely retrieves a string property from a JSON element, returning an
    ///     empty string if the property is not found or is null.
    /// </summary>
    /// <param name="element">The JSON element to retrieve the property from.</param>
    /// <param name="propertyName">The name of the property to retrieve.</param>
    /// <returns>The value of the property, or an empty string if not found or null.</returns>
    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }


    /// <summary>
    ///     Formats a byte value into a human-readable string in MiB with two decimal places.
    /// </summary>
    /// <param name="bytes">The byte value to format.</param>
    /// <returns>The formatted string.</returns>
    private static string FormatBytes(double bytes)
    {
        const double mebibyte = 1024 * 1024;
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / mebibyte:0.##} MiB");
    }


    /// <summary>
    ///     Constructs the Azure resource ID for the container app based on the
    ///     subscription ID, resource group name, and container app name.
    /// </summary>
    /// <param name="subscriptionId">The ID of the subscription.</param>
    /// <param name="resourceGroupName">The name of the resource group.</param>
    /// <param name="containerAppName">The name of the container app.</param>
    /// <returns>The resource ID of the container app.</returns>
    private static string BuildContainerAppResourceId(string subscriptionId, string resourceGroupName,
        string containerAppName)
    {
        return
            $"/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(containerAppName)}";
    }


    /// Represents the state of an Azure Container App, including the latest ready revision and FQDN.
    private sealed record ContainerAppState(string LatestRevision, string Fqdn);


    /// Represents the current resource metrics of an Azure Container App.
    private sealed record ContainerAppMetrics(string Cpu, string Memory);
}