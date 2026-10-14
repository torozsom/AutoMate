using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Azure.Monitor;

/// <summary>
///     Queries Azure Monitor Logs through its resource-scoped data-plane API.
/// </summary>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
internal sealed class AzureMonitorLogsClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>Official Log Analytics API base endpoint used for verified resource-scoped queries.</summary>
    private const string Endpoint = "https://api.loganalytics.azure.com/v1";

    /// <summary>
    ///     Queries bounded Container App observations using verified environment, app and revision
    ///     scope.
    /// </summary>
    /// <param name="resourceId">Verified Azure resource identity used to scope provider reads.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="source">Verified source classification, resource identity or owned input stream.</param>
    /// <param name="containerAppName">Verified Azure Container App name.</param>
    /// <param name="fromUtc">Absolute UTC lower bound for provider observation reads.</param>
    /// <param name="batchSize">Maximum number of records processed in one batch.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <param name="revision">Exact verified Azure revision identity.</param>
    /// <param name="afterTimestamp">Last durably accepted ingestion timestamp.</param>
    /// <param name="afterHash">Stable tie-breaker for records sharing the last ingestion timestamp.</param>
    /// <param name="environmentName">Runtime environment name used for deployment assets.</param>
    /// <param name="environmentResourceName">Verified managed-environment resource name.</param>
    /// <param name="includeAppEvents">Whether the query includes Container App event observations.</param>
    /// <returns>A task returning normalized records or fixed provider-availability guidance.</returns>
    public async Task<AzureMonitorLogQueryResult> QueryAsync(string resourceId, string accessToken,
        AzureContainerAppLogSource source, string containerAppName, DateTimeOffset fromUtc, int batchSize,
        CancellationToken cancellationToken, string? revision = null, DateTimeOffset? afterTimestamp = null,
        string? afterHash = null, string? environmentName = null, string? environmentResourceName = null,
        bool includeAppEvents = false)
    {
        var query = CreateQuery(source, containerAppName, fromUtc, batchSize, revision, afterTimestamp, afterHash,
            environmentName, environmentResourceName, includeAppEvents);
        return await SendQueryAsync(resourceId, accessToken, query, cancellationToken);
    }

    /// <summary>
    ///     Reads managed database resource logs exported to the shared AzureDiagnostics table.
    /// </summary>
    /// <param name="resourceId">Verified Azure resource identity used to scope provider reads.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="fromUtc">Absolute UTC lower bound for provider observation reads.</param>
    /// <param name="batchSize">Maximum number of records processed in one batch.</param>
    /// <param name="afterTimestamp">Last durably accepted ingestion timestamp.</param>
    /// <param name="afterHash">Stable tie-breaker for records sharing the last ingestion timestamp.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning normalized records or fixed provider-availability guidance.</returns>
    internal Task<AzureMonitorLogQueryResult> QueryDatabaseAsync(string resourceId, string accessToken,
        DateTimeOffset fromUtc, int batchSize, DateTimeOffset? afterTimestamp, string? afterHash,
        CancellationToken cancellationToken)
    {
        var id = resourceId.Replace("'", "''", StringComparison.Ordinal);
        var tables = resourceId.Contains("/Microsoft.DocumentDB/", StringComparison.OrdinalIgnoreCase)
            ? "union isfuzzy=true AzureDiagnostics, CDBMongoRequests"
            : "AzureDiagnostics";
        var query = $$"""
                      {{tables}}
                      | where _ResourceId =~ '{{id}}'
                      | extend IngestedAt=bin(coalesce(ingestion_time(), TimeGenerated), 1microsecond)
                      | where IngestedAt >= datetime({{fromUtc.UtcDateTime:O}})
                      | extend Category=coalesce(tostring(column_ifexists("Category", "")), "MongoRequests")
                      | where Category in ("MongoRequests", "PostgreSQLLogs", "Errors", "Timeouts", "Deadlocks")
                      | extend Operation=coalesce(tostring(column_ifexists("OperationName", "")), tostring(column_ifexists("operationName_s", ""))), ErrorCode=coalesce(tostring(column_ifexists("ErrorCode", "")), tostring(column_ifexists("errorCode_s", "")), tostring(column_ifexists("statusCode_s", ""))), Duration=coalesce(tostring(column_ifexists("DurationMs", "")), tostring(column_ifexists("duration_s", ""))), RU=coalesce(tostring(column_ifexists("RequestCharge", "")), tostring(column_ifexists("requestCharge_s", "")))
                      | project TimeGenerated, IngestedAt, Message=strcat(Category, " operation=", substring(Operation, 0, 128), " code=", substring(ErrorCode, 0, 64), " duration=", substring(Duration, 0, 64), " RU=", substring(RU, 0, 64)), ContainerName=tostring(column_ifexists("Resource", "")), RevisionName="", Stream="system", SourceTable=Category, RecordIdentity=coalesce(tostring(column_ifexists("ActivityId", "")), tostring(column_ifexists("activityId_g", "")))
                      | extend CursorHash=hash_sha256(strcat(tostring(TimeGenerated), Message, ContainerName, RecordIdentity))
                      {{CursorFilter(afterTimestamp, afterHash)}}
                      | order by IngestedAt asc, CursorHash asc
                      | take {{Math.Clamp(batchSize, 1, 2000)}}
                      """;
        return SendQueryAsync(resourceId, accessToken, query, cancellationToken);
    }

    /// <summary>
    ///     Uses a provider-controlled resource scope and returns safe failures without response
    ///     bodies.
    /// </summary>
    /// <param name="resourceId">Verified Azure resource identity used to scope provider reads.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="query">Bounded provider query or immutable resource/source selection.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning normalized records or fixed provider-availability guidance.</returns>
    private async Task<AzureMonitorLogQueryResult> SendQueryAsync(string resourceId, string accessToken,
        string query, CancellationToken cancellationToken)
    {
        var uri = $"{Endpoint}{resourceId}/query";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "include-permissions=true");

        using var response = await httpClientFactory.CreateClient("AzureRuntime").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return AzureMonitorLogQueryResult.Failed(response.StatusCode,
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? "Azure Monitor Logs access was denied. Grant the connected user Log Analytics Reader access."
                    : $"Azure Monitor Logs query failed with HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("error", out _))
            return AzureMonitorLogQueryResult.Failed(response.StatusCode,
                "Azure Monitor Logs returned an incomplete query. Collection will retry without advancing its checkpoint.");
        return AzureMonitorLogQueryResult.Succeeded(ParseRecords(document.RootElement));
    }

    /// <summary>
    ///     Normalizes supported Azure query table rows into bounded runtime log records.
    /// </summary>
    /// <param name="root">
    ///     JSON value to validate or normalize without retaining arbitrary provider prose.
    /// </param>
    /// <returns>The records selection with its documented bounds and ordering.</returns>
    private static IReadOnlyList<AzureMonitorLogRecord> ParseRecords(JsonElement root)
    {
        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array ||
            tables.GetArrayLength() == 0)
            return [];
        var table = tables[0];
        if (!table.TryGetProperty("columns", out var columns) || !table.TryGetProperty("rows", out var rows))
            return [];
        var indexes = columns.EnumerateArray().Select((column, index) => new
        { Name = column.GetProperty("name").GetString(), Index = index })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToDictionary(item => item.Name!, item => item.Index, StringComparer.OrdinalIgnoreCase);

        var records = new List<AzureMonitorLogRecord>();
        foreach (var row in rows.EnumerateArray())
        {
            var values = row.EnumerateArray().ToArray();
            var timestampText = GetValue(values, indexes, "TimeGenerated");
            if (!DateTimeOffset.TryParse(timestampText, out var timestamp)) continue;
            records.Add(new AzureMonitorLogRecord(timestamp.ToUniversalTime(), GetValue(values, indexes, "Message"),
                GetValue(values, indexes, "ContainerName"), GetValue(values, indexes, "RevisionName"),
                GetValue(values, indexes, "Stream"), GetValue(values, indexes, "SourceTable"),
                GetValue(values, indexes, "CursorHash") is { Length: > 0 } hash ? hash : null,
                DateTimeOffset.TryParse(GetValue(values, indexes, "IngestedAt"), out var ingested)
                    ? ingested.ToUniversalTime()
                    : null, GetValue(values, indexes, "ReplicaName")));
        }

        return records;
    }

    /// <summary>
    ///     Reads a named provider column without assuming its position is stable.
    /// </summary>
    /// <param name="values">The values to validate and copy.</param>
    /// <param name="indexes">Column-name to position mapping from the provider result.</param>
    /// <param name="name">The recorded display name or fixed metric identity.</param>
    /// <returns>The value derived from the validated input.</returns>
    private static string GetValue(JsonElement[] values, IReadOnlyDictionary<string, int> indexes, string name)
    {
        return indexes.TryGetValue(name, out var index) && index < values.Length &&
               values[index].ValueKind != JsonValueKind.Null
            ? values[index].ToString()
            : string.Empty;
    }

    /// <summary>
    ///     Builds a bounded Kusto query for the exact verified app, revision and ingestion cursor.
    /// </summary>
    /// <param name="source">Verified source classification, resource identity or owned input stream.</param>
    /// <param name="containerAppName">Verified Azure Container App name.</param>
    /// <param name="fromUtc">Absolute UTC lower bound for provider observation reads.</param>
    /// <param name="batchSize">Maximum number of records processed in one batch.</param>
    /// <param name="revision">Exact verified Azure revision identity.</param>
    /// <param name="afterTimestamp">Last durably accepted ingestion timestamp.</param>
    /// <param name="afterHash">Stable tie-breaker for records sharing the last ingestion timestamp.</param>
    /// <param name="environmentName">Runtime environment name used for deployment assets.</param>
    /// <param name="environmentResourceName">Verified managed-environment resource name.</param>
    /// <param name="includeAppEvents">Whether the query includes Container App event observations.</param>
    /// <returns>The bounded provider query or filter for the verified scope.</returns>
    private static string CreateQuery(AzureContainerAppLogSource source, string containerAppName,
        DateTimeOffset fromUtc, int batchSize, string? revision, DateTimeOffset? afterTimestamp, string? afterHash,
        string? environmentName = null, string? environmentResourceName = null, bool includeAppEvents = false)
    {
        var appName = containerAppName.Replace("'", "''", StringComparison.Ordinal);
        var from = fromUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var safeBatchSize = Math.Clamp(batchSize, 1, 2_000);
        var environmentFilter = string.IsNullOrWhiteSpace(environmentName)
            ? ""
            : "| where coalesce(tostring(column_ifexists('EnvironmentName', '')), tostring(column_ifexists('EnvironmentName_s', ''))) in ('" +
              environmentName.Replace("'", "''", StringComparison.Ordinal) + "', '" +
              (environmentResourceName ?? environmentName).Replace("'", "''", StringComparison.Ordinal) + "')";
        var tablePattern = source == AzureContainerAppLogSource.Console
            ? "ContainerAppConsoleLogs*"
            : "ContainerAppSystemLogs*";
        var stream = source == AzureContainerAppLogSource.Console
            ? "coalesce(tostring(column_ifexists(\"Stream\", \"\")), tostring(column_ifexists(\"Stream_s\", \"\")))"
            : "'system'";
        var branches = $$"""
                         union withsource=SourceTable {{tablePattern}}
                         {{environmentFilter}}
                         | extend AppName=coalesce(tostring(column_ifexists("ContainerAppName", "")), tostring(column_ifexists("ContainerAppName_s", "")))
                         | where AppName == '{{appName}}'
                         | extend IngestedAt=bin(coalesce(ingestion_time(), TimeGenerated), 1microsecond)
                         | project TimeGenerated, IngestedAt, Message=coalesce(tostring(column_ifexists("Log", "")), tostring(column_ifexists("Log_s", "")), tostring(column_ifexists("Message", "")), tostring(column_ifexists("Message_s", "")), tostring(column_ifexists("Reason", "")), tostring(column_ifexists("Reason_s", ""))), ContainerName=coalesce(tostring(column_ifexists("ContainerName", "")), tostring(column_ifexists("ContainerName_s", ""))), RevisionName=coalesce(tostring(column_ifexists("RevisionName", "")), tostring(column_ifexists("RevisionName_s", ""))), Stream={{stream}}, ReplicaName=coalesce(tostring(column_ifexists("ReplicaName", "")), tostring(column_ifexists("ReplicaName_s", "")), tostring(column_ifexists("ContainerGroupName_s", "")), tostring(column_ifexists("ContainerGroupName_g", ""))), SourceTable
                         """;
        var revisionFilter = string.IsNullOrWhiteSpace(revision)
            ? ""
            : "| where RevisionName == '" + revision.Replace("'", "''", StringComparison.Ordinal) + "'" +
              (source == AzureContainerAppLogSource.System && includeAppEvents ? " or isempty(RevisionName)" : "");
        var cursorFilter = CursorFilter(afterTimestamp, afterHash);
        return $$"""
                 {{branches}}
                 | where IngestedAt >= datetime({{from}})
                 {{revisionFilter}}
                 | extend CursorHash=hash_sha256(strcat(tostring(TimeGenerated), '\n', SourceTable, '\n', Message, '\n', ContainerName, '\n', RevisionName, '\n', Stream))
                 {{cursorFilter}}
                 | order by IngestedAt asc, CursorHash asc
                 | take {{safeBatchSize}}
                 """;
    }

    /// <summary>
    ///     Uses ingestion ordering so late events are not lost behind the event-time watermark.
    /// </summary>
    /// <param name="timestamp">The provider observation timestamp, if known.</param>
    /// <param name="hash">Stable content hash used for confirmation or deduplication.</param>
    /// <returns>The bounded provider query or filter for the verified scope.</returns>
    private static string CursorFilter(DateTimeOffset? timestamp, string? hash)
    {
        return timestamp is null
            ? ""
            : $"| where IngestedAt > datetime({timestamp.Value.UtcDateTime:O}) or (IngestedAt == datetime({timestamp.Value.UtcDateTime:O}) and strcmp(CursorHash, '{(hash ?? "").Replace("'", "''", StringComparison.Ordinal)}') > 0)";
    }
}

/// <summary>
///     Distinguishes application console output from Azure platform/system output.
/// </summary>
internal enum AzureContainerAppLogSource
{
    /// <summary>Application container console output.</summary>
    Console,

    /// <summary>Azure platform and revision lifecycle output.</summary>
    System
}

/// <summary>
///     Normalized Azure observation with original event time and an independent ingestion
///     cursor.
/// </summary>
/// <param name="TimestampUtc">The original UTC observation timestamp, if recorded.</param>
/// <param name="Message">The message supplied to the operation.</param>
/// <param name="ContainerName">
///     Verified Azure container or Container App identity used to scope the query.
/// </param>
/// <param name="RevisionName">Exact verified Azure revision name.</param>
/// <param name="Stream">The typed diagnostic output stream.</param>
/// <param name="SourceTable">Recorded Azure diagnostic category or table.</param>
/// <param name="CursorHash">Stable hash ordering records with equal ingestion times.</param>
/// <param name="IngestionTimestampUtc">Azure ingestion time used to detect delayed records.</param>
/// <param name="ReplicaName">Verified Container App replica name.</param>
internal sealed record AzureMonitorLogRecord(
    DateTimeOffset TimestampUtc,
    string Message,
    string ContainerName,
    string RevisionName,
    string Stream,
    string SourceTable,
    string? CursorHash = null,
    DateTimeOffset? IngestionTimestampUtc = null,
    string ReplicaName = "");

/// <summary>
///     Bounded Azure query records or fixed source-availability guidance.
/// </summary>
/// <param name="Records">Bounded normalized Azure log observations.</param>
/// <param name="FailureReason">A fixed, safe failure reason, if available.</param>
internal sealed record AzureMonitorLogQueryResult(IReadOnlyList<AzureMonitorLogRecord> Records, string? FailureReason)
{
    /// <summary>Whether the query returned records without a fixed failure reason.</summary>
    public bool IsSuccess => FailureReason is null;

    /// <summary>
    ///     Creates a successful query result with its normalized observations.
    /// </summary>
    /// <param name="records">Bounded normalized Azure log observations.</param>
    /// <returns>A successful query result containing the supplied normalized records.</returns>
    public static AzureMonitorLogQueryResult Succeeded(IReadOnlyList<AzureMonitorLogRecord> records)
    {
        return new AzureMonitorLogQueryResult(records, null);
    }

    /// <summary>
    ///     Creates fixed failure guidance without retaining arbitrary Azure response bodies.
    /// </summary>
    /// <param name="_">HTTP status deliberately excluded from the fixed failure response.</param>
    /// <param name="reason">
    ///     Fixed safe availability or failure guidance; never raw provider response text.
    /// </param>
    /// <returns>A query result with no records and the supplied fixed failure guidance.</returns>
    public static AzureMonitorLogQueryResult Failed(HttpStatusCode _, string reason)
    {
        return new AzureMonitorLogQueryResult([], reason);
    }
}
