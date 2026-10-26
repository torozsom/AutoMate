using System.Net.Http.Headers;
using System.Text.Json;
using Application.Abstractions.Diagnostics.Metrics;
using Infrastructure.Azure.ContainerApps;
using Infrastructure.Azure.Monitor;

namespace Infrastructure.Azure.Databases;

/// <summary>
///     Discovers only AutoMate-tagged databases in a deployment's resource group and reads
///     native metrics.
/// </summary>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
internal sealed class AzureManagedDatabaseClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>
    ///     Lists a bounded inventory scoped by project and Container App ownership tags.
    /// </summary>
    /// <param name="appResourceId">Verified Container App ARM resource identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="appName">Application name used for deployment artifacts.</param>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <param name="databaseComponents">Recorded component suffixes required to associate dedicated database apps.</param>
    /// <returns>A task returning the discover async selection with its documented bounds and ordering.</returns>
    /// <exception cref="InvalidOperationException">Database inventory exceeds the collection limit.</exception>
    internal async Task<IReadOnlyList<AzureManagedDatabase>> DiscoverAsync(string appResourceId, Guid projectId,
        string appName, string token, CancellationToken cancellationToken,
        IReadOnlySet<string>? databaseComponents = null)
    {
        var group = appResourceId[..appResourceId.IndexOf("/providers/", StringComparison.OrdinalIgnoreCase)];

        var resources = await ListAsync(group +
                                        "/resources?api-version=2021-04-01", group, token, cancellationToken);

        foreach (var server in resources
                     .Where(r => string.Equals(
                         r.GetProperty("type").GetString(),
                         "Microsoft.Sql/servers", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var id = server.GetProperty("id").GetString()!;

            if (id.StartsWith(group + "/providers/Microsoft.Sql/servers/", StringComparison.OrdinalIgnoreCase))
                resources.AddRange(await ListAsync(id + "/databases?api-version=2023-08-01", group, token,
                    cancellationToken));
        }

        var result = new List<AzureManagedDatabase>();

        foreach (var resource in resources)
        {
            if (!resource.TryGetProperty("tags", out var tags)
                || tags.ValueKind != JsonValueKind.Object
                || !tags.TryGetProperty("automate-project-id", out var owner)
                || owner.GetString() != projectId.ToString("N")
                || !tags.TryGetProperty("automate-container-app", out var app)
                || app.GetString() != appName) continue;

            var id = resource.GetProperty("id").GetString()!;

            if (!id.StartsWith(group + "/providers/", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(database =>
                    string.Equals(database.ResourceId, id, StringComparison.OrdinalIgnoreCase))) continue;

            var type = resource.GetProperty("type").GetString()!.ToLowerInvariant();

            if (type == "microsoft.app/containerapps")
            {
                // Dedicated database apps require explicit component association, never a name heuristic.
                if (string.Equals(id, appResourceId, StringComparison.OrdinalIgnoreCase) ||
                    !tags.TryGetProperty("automate-database-component", out var component) ||
                    databaseComponents?.Contains(component.GetString() ?? string.Empty) != true ||
                    !tags.TryGetProperty("automate-database-provider", out var provider) ||
                    provider.GetString() is not ("PostgreSQL" or "MySQL" or "SQLServer" or "Redis" or "MongoDB"))
                    continue;

                if (result.Count == 100)
                    throw new InvalidOperationException("Database inventory exceeds the collection limit.");

                var client = new AzureContainerAppClient(httpClientFactory);
                var state = await client.GetStateAsync(id, token, cancellationToken);

                if (string.IsNullOrWhiteSpace(state?.LatestRevision))
                    throw new InvalidOperationException("Associated database app has no verified ready revision.");

                var logScope = string.IsNullOrWhiteSpace(state.ManagedEnvironmentId)
                    ? null
                    : await client.GetEnvironmentLogScopeAsync(state.ManagedEnvironmentId, token, cancellationToken);

                var name = resource.GetProperty("name").GetString()!;
                result.Add(new AzureManagedDatabase(id, name, "UsageNanoCores,WorkingSetBytes",
                    provider.GetString(), state.LatestRevision, logScope));
                continue;
            }

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

    /// <summary>
    ///     Preserves native percent/byte units and missing samples instead of inventing core usage.
    /// </summary>
    /// <param name="database">Selected owned database resource or deployment configuration.</param>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the metrics async selection with its documented bounds and ordering.</returns>
    internal async Task<IReadOnlyList<DeploymentMetricSample>> MetricsAsync(AzureManagedDatabase database,
        string token, CancellationToken cancellationToken)
    {
        return (await MetricObservationsAsync(database, token, cancellationToken)).LastOrDefault()?.Samples ?? [];
    }

    /// <summary>
    ///     Reads all native minute buckets, aggregating dimensions explicitly and leaving gaps
    ///     absent.
    /// </summary>
    /// <param name="database">Selected owned database resource or deployment configuration.</param>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the metric observations async selection with its documented bounds and
    ///     ordering.
    /// </returns>
    internal async Task<IReadOnlyList<AzureMetricObservation>> MetricObservationsAsync(AzureManagedDatabase database,
        string token, CancellationToken cancellationToken)
    {
        var end = AzureMetricObservations.CompletedMinute();

        // Azure Cache for Redis supports Maximum rather than Average for these gauges.
        var aggregation = database.MetricNames
                              .Contains("percentProcessorTime", StringComparison.Ordinal)
                          || database.MetricNames == "NormalizedRUConsumption"
            ? "Maximum"
            : "Average";

        var revisionFilter = database.ContainerAppRevision is { } revision
            ? "&$filter=" + Uri.EscapeDataString("revisionName eq '" +
                                                 revision.Replace("'", "''", StringComparison.Ordinal) +
                                                 "' and podName eq '*'")
            : string.Empty;

        using var document = await GetAsync(database.ResourceId +
                                            "/providers/microsoft.insights/metrics?api-version=2023-10-01&metricnames=" +
                                            database.MetricNames +
                                            "&timespan=" +
                                            Uri.EscapeDataString(
                                                $"{end.AddMinutes(-5).UtcDateTime:O}/{end.UtcDateTime:O}") +
                                            "&interval=PT1M&aggregation=" + aggregation + revisionFilter,
            token, cancellationToken);

        return AzureMetricObservations.Parse(document.RootElement, aggregation,
            database.ContainerAppRevision is not null);
    }

    /// <summary>
    ///     Bounds inventory pages and verifies every continuation before forwarding a bearer token.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="group">Fixed terminal or provider query group.</param>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <param name="ct">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the list async selection with its documented bounds and ordering.</returns>
    /// <exception cref="InvalidOperationException">Azure inventory exceeds the resource limit.</exception>
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

    /// <summary>
    ///     Rejects failed ARM reads; callers publish a finite availability notice and retry.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the owned parsed JSON response; the caller must dispose it.</returns>
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

/// <summary>
///     Non-secret verified database identity and provider metric names; never connection
///     strings.
/// </summary>
/// <param name="ResourceId">Verified Azure resource identity used to scope provider reads.</param>
/// <param name="Name">The recorded display name or fixed metric identity.</param>
/// <param name="MetricNames">Fixed metric-series names requested from the provider.</param>
/// <param name="ConfiguredProvider">Explicit provider association for a dedicated database Container App.</param>
/// <param name="ContainerAppRevision">Ready revision read from the associated app, never borrowed from the web app.</param>
/// <param name="LogScope">ARM-resolved log environment for a dedicated database app.</param>
internal sealed record AzureManagedDatabase(
    string ResourceId,
    string Name,
    string MetricNames,
    string? ConfiguredProvider = null,
    string? ContainerAppRevision = null,
    AzureContainerAppLogScope? LogScope = null)
{
    /// <summary>Matches the resource's verified type to the recorded deployment provider composition.</summary>
    internal string Provider => ConfiguredProvider ?? ResourceId.ToLowerInvariant() switch
    {
        var id when id.Contains("/microsoft.dbforpostgresql/", StringComparison.Ordinal) => "PostgreSQL",
        var id when id.Contains("/microsoft.dbformysql/", StringComparison.Ordinal) => "MySQL",
        var id when id.Contains("/microsoft.sql/", StringComparison.Ordinal) => "SQLServer",
        var id when id.Contains("/microsoft.cache/", StringComparison.Ordinal) => "Redis",
        var id when id.Contains("/microsoft.documentdb/", StringComparison.Ordinal) => "MongoDB",
        _ => "unknown"
    };

    /// <summary>Operational logs exist without requiring opt-in audit or slow-query categories.</summary>
    internal bool HasOperationalLogs =>
        ContainerAppRevision is not null ||
        ResourceId.Contains("/Microsoft.DocumentDB/", StringComparison.OrdinalIgnoreCase) ||
        ResourceId.Contains("/Microsoft.DBforPostgreSQL/", StringComparison.OrdinalIgnoreCase) ||
        ResourceId.Contains("/Microsoft.Sql/", StringComparison.OrdinalIgnoreCase);
}
