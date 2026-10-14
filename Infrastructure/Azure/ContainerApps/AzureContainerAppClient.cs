using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Infrastructure.Azure.Monitor;

namespace Infrastructure.Azure.ContainerApps;

/// <summary>
///     Reads Azure Container Apps availability and metrics through Azure Resource Manager REST
///     APIs.
/// </summary>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
internal sealed class AzureContainerAppClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>API version used for Container Apps resource state reads.</summary>
    private const string ContainerAppApiVersion = "2024-03-01";

    /// <summary>API version used for Azure Monitor metrics reads.</summary>
    private const string MetricsApiVersion = "2023-10-01";

    /// <summary>Azure Monitor metric name for Container Apps CPU usage.</summary>
    private const string CpuMetricName = "UsageNanoCores";

    /// <summary>Azure Monitor metric name for Container Apps working set memory.</summary>
    private const string MemoryMetricName = "WorkingSetBytes";

    /// <summary>Display value used when Azure returns no recent metric sample.</summary>
    private const string UnknownMetricValue = "n/a";

    /// <summary>
    ///     Resolves the owning environment's workspace for legacy custom tables with no resource
    ///     attribution.
    /// </summary>
    /// <param name="environmentId">Verified Azure managed-environment ARM identity.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning verified managed-environment, app and revision scope, or null when no
    ///     value is available.
    /// </returns>
    internal async Task<AzureContainerAppLogScope?> GetEnvironmentLogScopeAsync(string environmentId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var document = await SendRequestAsync(
            $"{AzureConstants.ManagementEndpoint}{environmentId}?api-version={ContainerAppApiVersion}",
            accessToken, cancellationToken);
        if (document is null) return null;
        if (document.RootElement.TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("appLogsConfiguration", out var logs) &&
            logs.TryGetProperty("logAnalyticsConfiguration", out var analytics) &&
            analytics.TryGetProperty("customerId", out var customer) &&
            Guid.TryParse(customer.GetString(), out var workspace))
        {
            var domain = GetString(properties, "defaultDomain");
            if (string.IsNullOrWhiteSpace(domain)) return null;
            return new AzureContainerAppLogScope($"/workspaces/{workspace:D}", domain.Split('.')[0],
                environmentId.Split('/').Last());
        }

        return new AzureContainerAppLogScope(environmentId, null, null);
    }

    /// <summary>
    ///     Reads the latest ready revision and ingress FQDN for a Container App.
    /// </summary>
    /// <param name="resourceId">Verified Azure resource identity used to scope provider reads.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning recorded resource and revision state from the verified provider scope,
    ///     or null when no value is available.
    /// </returns>
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

        string? image = null;
        if (properties.TryGetProperty("template", out var template) &&
            template.TryGetProperty("containers", out var containers) &&
            containers.ValueKind == JsonValueKind.Array && containers.GetArrayLength() > 0)
            image = GetString(containers[0], "image");
        return new AzureContainerAppState(latestRevision, fqdn, image, GetString(properties, "managedEnvironmentId"));
    }

    /// <summary>
    ///     Reads recent average CPU and memory metrics for a Container App.
    /// </summary>
    /// <param name="resourceId">Verified Azure resource identity used to scope provider reads.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <param name="revision">Exact verified Azure revision identity.</param>
    /// <returns>
    ///     A task returning observed native CPU and memory samples, preserving unsupported values,
    ///     or null when no value is available.
    /// </returns>
    public async Task<AzureContainerAppMetrics?> GetMetricsAsync(string resourceId, string accessToken,
        CancellationToken cancellationToken, string? revision = null)
    {
        var endTime = AzureMetricObservations.CompletedMinute();
        var startTime = endTime.AddMinutes(-5);
        var requestUri = AzureConstants.ManagementEndpoint + resourceId +
                         $"/providers/microsoft.insights/metrics?api-version={MetricsApiVersion}" +
                         $"&metricnames={CpuMetricName},{MemoryMetricName}" +
                         $"&timespan={Uri.EscapeDataString($"{startTime.UtcDateTime:O}/{endTime.UtcDateTime:O}")}&interval=PT1M&aggregation=Average";
        if (!string.IsNullOrWhiteSpace(revision))
            requestUri += "&$filter=" + Uri.EscapeDataString("revisionName eq '" +
                                                             revision.Replace("'", "''", StringComparison.Ordinal) +
                                                             "' and podName eq '*'");

        using var document = await SendRequestAsync(requestUri, accessToken, cancellationToken);
        if (document == null)
            return null;

        var cpu = UnknownMetricValue;
        var memory = UnknownMetricValue;
        double? cpuCores = null;
        double? memoryBytes = null;

        if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        var observations = AzureMetricObservations.Parse(document.RootElement, "Average", true);
        var latest = observations.LastOrDefault();
        cpuCores = latest?.Samples.FirstOrDefault(s => s.Unit == "cores")?.Value;
        memoryBytes = latest?.Samples.FirstOrDefault(s => s.Unit == "bytes")?.Value;
        if (cpuCores is { } cores) cpu = string.Create(CultureInfo.InvariantCulture, $"{cores:0.##} cores");
        if (memoryBytes is { } bytes) memory = FormatBytes(bytes);
        return new AzureContainerAppMetrics(cpu, memory, cpuCores, memoryBytes, observations);
    }

    /// <summary>
    ///     Sends an authenticated ARM GET request and returns parsed JSON when Azure responds
    ///     successfully.
    /// </summary>
    /// <param name="requestUri">Validated provider request URI.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the owned parsed JSON response; the caller must dispose it, or null
    ///     when no value is available.
    /// </returns>
    private async Task<JsonDocument?> SendRequestAsync(string requestUri, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClientFactory.CreateClient("AzureRuntime").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     Reads an optional JSON string property and normalizes missing values to an empty string.
    /// </summary>
    /// <param name="element">
    ///     JSON value to validate or normalize without retaining arbitrary provider prose.
    /// </param>
    /// <param name="propertyName">Project XML or JSON property name to inspect.</param>
    /// <returns>The string derived from the validated input.</returns>
    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    ///     Formats byte values from Azure Monitor as mebibytes for log stream display.
    /// </summary>
    /// <param name="bytes">The maximum encoded UTF-8 size of provider input.</param>
    /// <returns>The bytes derived from the validated input.</returns>
    private static string FormatBytes(double bytes)
    {
        const double mebibyte = 1024 * 1024;
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / mebibyte:0.##} MiB");
    }
}
