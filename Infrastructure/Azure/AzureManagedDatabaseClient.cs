using System.Net.Http.Headers;
using System.Text.Json;
using Application.Abstractions.Diagnostics;

namespace Infrastructure.Azure;

/// <summary>Discovers only AutoMate-tagged databases in a deployment's resource group and reads native metrics.</summary>
internal sealed class AzureManagedDatabaseClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>Lists a bounded inventory scoped by project and Container App ownership tags.</summary>
    internal async Task<IReadOnlyList<AzureManagedDatabase>> DiscoverAsync(string appResourceId, Guid projectId,
        string appName, string token, CancellationToken cancellationToken)
    {
        var group = appResourceId[..appResourceId.IndexOf("/providers/", StringComparison.OrdinalIgnoreCase)];
        var resources = await ListAsync(group + "/resources?api-version=2021-04-01", group, token, cancellationToken);
        foreach (var server in resources.Where(r => string.Equals(r.GetProperty("type").GetString(),
                     "Microsoft.Sql/servers", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var id = server.GetProperty("id").GetString()!;
            if (id.StartsWith(group + "/providers/Microsoft.Sql/servers/", StringComparison.OrdinalIgnoreCase))
                resources.AddRange(await ListAsync(id + "/databases?api-version=2023-08-01", group, token,
                    cancellationToken));
        }

        var result = new List<AzureManagedDatabase>();
        foreach (var resource in resources)
        {
            if (!resource.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object ||
                !tags.TryGetProperty("automate-project-id", out var owner) ||
                owner.GetString() != projectId.ToString("N") ||
                !tags.TryGetProperty("automate-container-app", out var app) || app.GetString() != appName) continue;
            var id = resource.GetProperty("id").GetString()!;
            if (!id.StartsWith(group + "/providers/", StringComparison.OrdinalIgnoreCase)) continue;
            var type = resource.GetProperty("type").GetString()!.ToLowerInvariant();
            var metrics = type switch
            {
                "microsoft.dbforpostgresql/flexibleservers" or "microsoft.dbformysql/flexibleservers" =>
                    "cpu_percent,memory_percent",
                "microsoft.sql/servers/databases" => "cpu_percent",
                "microsoft.cache/redis" => "percentProcessorTime,usedmemory",
                "microsoft.documentdb/databaseaccounts" => "NormalizedRUConsumption",
                _ => null
            };
            if (metrics is null ||
                result.Any(d => string.Equals(d.ResourceId, id, StringComparison.OrdinalIgnoreCase))) continue;
            if (result.Count == 100)
                throw new InvalidOperationException("Database inventory exceeds the collection limit.");
            result.Add(new AzureManagedDatabase(id, resource.GetProperty("name").GetString()!, metrics));
        }

        return result;
    }

    /// <summary>Preserves native percent/byte units and missing samples instead of inventing core usage.</summary>
    internal async Task<IReadOnlyList<DeploymentMetricSample>> MetricsAsync(AzureManagedDatabase database,
        string token, CancellationToken cancellationToken)
    {
        return (await MetricObservationsAsync(database, token, cancellationToken)).LastOrDefault()?.Samples ?? [];
    }

    /// <summary>Reads all native minute buckets, aggregating dimensions explicitly and leaving gaps absent.</summary>
    internal async Task<IReadOnlyList<AzureMetricObservation>> MetricObservationsAsync(AzureManagedDatabase database,
        string token, CancellationToken cancellationToken)
    {
        var end = AzureMetricObservations.CompletedMinute();
        // Azure Cache for Redis supports Maximum rather than Average for these gauges.
        var aggregation = database.MetricNames.Contains("percentProcessorTime", StringComparison.Ordinal) ||
                          database.MetricNames == "NormalizedRUConsumption"
            ? "Maximum"
            : "Average";
        using var document = await GetAsync(database.ResourceId +
                                            "/providers/microsoft.insights/metrics?api-version=2023-10-01&metricnames=" +
                                            database.MetricNames +
                                            "&timespan=" +
                                            Uri.EscapeDataString(
                                                $"{end.AddMinutes(-5).UtcDateTime:O}/{end.UtcDateTime:O}") +
                                            "&interval=PT1M&aggregation=" + aggregation,
            token, cancellationToken);
        return AzureMetricObservations.Parse(document.RootElement, aggregation, false);
    }

    /// <summary>Bounds inventory pages and verifies every continuation before forwarding a bearer token.</summary>
    private async Task<List<JsonElement>> ListAsync(string path, string group, string token, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        for (var page = 0; page < 10; page++)
        {
            using var document = await GetAsync(path, token, ct);
            if (document.RootElement.TryGetProperty("value", out var resources))
                result.AddRange(resources.EnumerateArray().Select(r => r.Clone()));
            if (result.Count > 1000) throw new InvalidOperationException("Azure inventory exceeds the resource limit.");
            if (!document.RootElement.TryGetProperty("nextLink", out var next) ||
                string.IsNullOrEmpty(next.GetString())) return result;
            var uri = new Uri(next.GetString()!, UriKind.Absolute);
            if (uri.Scheme != "https" || uri.Host != "management.azure.com" || !uri.IsDefaultPort ||
                uri.UserInfo.Length > 0 ||
                !uri.AbsolutePath.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Azure continuation is outside its authorized resource group.");
            path = uri.PathAndQuery;
        }

        throw new InvalidOperationException("Azure inventory exceeds the page limit.");
    }

    /// <summary>Rejects failed ARM reads; callers publish a finite availability notice and retry.</summary>
    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AzureConstants.ManagementEndpoint + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClientFactory.CreateClient("AzureRuntime").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}

/// <summary>Non-secret verified database identity and provider metric names; never connection strings.</summary>
internal sealed record AzureManagedDatabase(string ResourceId, string Name, string MetricNames)
{
    /// <summary>Operational logs exist without requiring opt-in audit or slow-query categories.</summary>
    internal bool HasOperationalLogs =>
        ResourceId.Contains("/Microsoft.DocumentDB/", StringComparison.OrdinalIgnoreCase) ||
        ResourceId.Contains("/Microsoft.DBforPostgreSQL/", StringComparison.OrdinalIgnoreCase) ||
        ResourceId.Contains("/Microsoft.Sql/", StringComparison.OrdinalIgnoreCase);
}