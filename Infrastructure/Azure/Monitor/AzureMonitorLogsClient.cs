using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Azure;

/// <summary>Queries Azure Monitor Logs through its resource-scoped data-plane API.</summary>
internal sealed class AzureMonitorLogsClient(IHttpClientFactory httpClientFactory)
{
    private const string Endpoint = "https://api.loganalytics.azure.com/v1";

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

    /// <summary>Reads managed database resource logs exported to the shared AzureDiagnostics table.</summary>
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

    /// <summary>Uses a provider-controlled resource scope and returns safe failures without response bodies.</summary>
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

    private static string GetValue(JsonElement[] values, IReadOnlyDictionary<string, int> indexes, string name)
    {
        return indexes.TryGetValue(name, out var index) && index < values.Length &&
               values[index].ValueKind != JsonValueKind.Null
            ? values[index].ToString()
            : string.Empty;
    }

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

    /// <summary>Uses ingestion ordering so late events are not lost behind the event-time watermark.</summary>
    private static string CursorFilter(DateTimeOffset? timestamp, string? hash)
    {
        return timestamp is null
            ? ""
            : $"| where IngestedAt > datetime({timestamp.Value.UtcDateTime:O}) or (IngestedAt == datetime({timestamp.Value.UtcDateTime:O}) and strcmp(CursorHash, '{(hash ?? "").Replace("'", "''", StringComparison.Ordinal)}') > 0)";
    }
}

internal enum AzureContainerAppLogSource
{
    Console,
    System
}

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

internal sealed record AzureMonitorLogQueryResult(IReadOnlyList<AzureMonitorLogRecord> Records, string? FailureReason)
{
    public bool IsSuccess => FailureReason is null;

    public static AzureMonitorLogQueryResult Succeeded(IReadOnlyList<AzureMonitorLogRecord> records)
    {
        return new AzureMonitorLogQueryResult(records, null);
    }

    public static AzureMonitorLogQueryResult Failed(HttpStatusCode _, string reason)
    {
        return new AzureMonitorLogQueryResult([], reason);
    }
}