using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Services.Azure;

/// <summary>
///     Reads Azure Container Apps availability and metrics through Azure Resource Manager REST APIs.
/// </summary>
internal sealed class AzureContainerAppClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>
    ///     API version used for Container Apps resource state reads.
    /// </summary>
    private const string ContainerAppApiVersion = "2024-03-01";

    /// <summary>
    ///     API version used for Azure Monitor metrics reads.
    /// </summary>
    private const string MetricsApiVersion = "2023-10-01";

    /// <summary>
    ///     Azure Monitor metric name for Container Apps CPU usage.
    /// </summary>
    private const string CpuMetricName = "CpuUsage";

    /// <summary>
    ///     Azure Monitor metric name for Container Apps working set memory.
    /// </summary>
    private const string MemoryMetricName = "MemoryWorkingSet";

    /// <summary>
    ///     Display value used when Azure returns no recent metric sample.
    /// </summary>
    private const string UnknownMetricValue = "n/a";

    /// <summary>
    ///     Reads the latest ready revision and ingress FQDN for a Container App.
    /// </summary>
    public async Task<AzureContainerAppState?> GetStateAsync(string resourceId, string accessToken,
        CancellationToken cancellationToken)
    {
        var requestUri = $"{AzureConstants.ManagementEndpoint}{resourceId}?api-version={ContainerAppApiVersion}";
        using var document = await SendRequestAsync(requestUri, accessToken, cancellationToken);
        if (document == null)
            return null;

        if (!document.RootElement.TryGetProperty("properties", out var properties))
            return null;

        var latestRevision = GetString(properties, "latestReadyRevisionName");
        var fqdn = string.Empty;

        if (properties.TryGetProperty("configuration", out var configuration) &&
            configuration.TryGetProperty("ingress", out var ingress))
            fqdn = GetString(ingress, "fqdn");

        return new AzureContainerAppState(latestRevision, fqdn);
    }

    /// <summary>
    ///     Reads recent average CPU and memory metrics for a Container App.
    /// </summary>
    public async Task<AzureContainerAppMetrics?> GetMetricsAsync(string resourceId, string accessToken,
        CancellationToken cancellationToken)
    {
        var endTime = DateTimeOffset.UtcNow;
        var startTime = endTime.AddMinutes(-5);
        var requestUri = AzureConstants.ManagementEndpoint + resourceId +
                         $"/providers/microsoft.insights/metrics?api-version={MetricsApiVersion}" +
                         $"&metricnames={CpuMetricName},{MemoryMetricName}" +
                         $"&timespan={Uri.EscapeDataString($"{startTime:O}/{endTime:O}")}&interval=PT1M&aggregation=Average";

        using var document = await SendRequestAsync(requestUri, accessToken, cancellationToken);
        if (document == null)
            return null;

        var cpu = UnknownMetricValue;
        var memory = UnknownMetricValue;

        if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var metric in values.EnumerateArray())
        {
            var name = metric.GetProperty("name").GetProperty("value").GetString();
            var latestAverage = GetLatestAverage(metric);
            if (latestAverage == null)
                continue;

            if (string.Equals(name, CpuMetricName, StringComparison.OrdinalIgnoreCase))
                cpu = $"{latestAverage.Value:0.##} cores";
            else if (string.Equals(name, MemoryMetricName, StringComparison.OrdinalIgnoreCase))
                memory = FormatBytes(latestAverage.Value);
        }

        return new AzureContainerAppMetrics(cpu, memory);
    }

    /// <summary>
    ///     Sends an authenticated ARM GET request and returns parsed JSON when Azure responds successfully.
    /// </summary>
    private async Task<JsonDocument?> SendRequestAsync(string requestUri, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     Finds the most recent average sample in an Azure Monitor metric payload.
    /// </summary>
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
    ///     Reads an optional JSON string property and normalizes missing values to an empty string.
    /// </summary>
    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    ///     Formats byte values from Azure Monitor as mebibytes for log stream display.
    /// </summary>
    private static string FormatBytes(double bytes)
    {
        const double mebibyte = 1024 * 1024;
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / mebibyte:0.##} MiB");
    }
}