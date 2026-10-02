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
        CancellationToken cancellationToken)
    {
        var uri = $"{Endpoint}{resourceId}/query";
        var query = CreateQuery(source, containerAppName, fromUtc, batchSize);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "include-permissions=true");

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return AzureMonitorLogQueryResult.Failed(response.StatusCode,
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? "Azure Monitor Logs access was denied. Grant the connected user Log Analytics Reader access."
                    : $"Azure Monitor Logs query failed with HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
                GetValue(values, indexes, "Stream"), GetValue(values, indexes, "SourceTable")));
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
        DateTimeOffset fromUtc, int batchSize)
    {
        var appName = containerAppName.Replace("'", "''", StringComparison.Ordinal);
        var from = fromUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var safeBatchSize = Math.Clamp(batchSize, 1, 2_000);
        var branches = source == AzureContainerAppLogSource.Console
            ? $$"""
                (ContainerAppConsoleLogs | where ContainerAppName == '{{appName}}' | project TimeGenerated, Message=tostring(Log), ContainerName=tostring(ContainerName), RevisionName=tostring(RevisionName), Stream=tostring(Stream), SourceTable="ContainerAppConsoleLogs"),
                (ContainerAppConsoleLogs_CL | where ContainerAppName_s == '{{appName}}' | project TimeGenerated, Message=tostring(Log_s), ContainerName=tostring(ContainerName_s), RevisionName=tostring(RevisionName_s), Stream=tostring(column_ifexists("Stream_s", "")), SourceTable="ContainerAppConsoleLogs_CL")
                """
            : $$"""
                (ContainerAppSystemLogs | where ContainerAppName == '{{appName}}' | project TimeGenerated, Message=case(isnotempty(tostring(column_ifexists("Log", ""))), tostring(column_ifexists("Log", "")), isnotempty(tostring(column_ifexists("Message", ""))), tostring(column_ifexists("Message", "")), tostring(column_ifexists("Reason", ""))), ContainerName=tostring(column_ifexists("ContainerName", "")), RevisionName=tostring(column_ifexists("RevisionName", "")), Stream="system", SourceTable="ContainerAppSystemLogs"),
                (ContainerAppSystemLogs_CL | where ContainerAppName_s == '{{appName}}' | project TimeGenerated, Message=case(isnotempty(tostring(column_ifexists("Log_s", ""))), tostring(column_ifexists("Log_s", "")), isnotempty(tostring(column_ifexists("Message_s", ""))), tostring(column_ifexists("Message_s", "")), tostring(column_ifexists("Reason_s", ""))), ContainerName=tostring(column_ifexists("ContainerName_s", "")), RevisionName=tostring(column_ifexists("RevisionName_s", "")), Stream="system", SourceTable="ContainerAppSystemLogs_CL")
                """;

        return $$"""
                 union isfuzzy=true {{branches}}
                 | where TimeGenerated >= datetime({{from}})
                 | order by TimeGenerated asc, Message asc
                 | take {{safeBatchSize}}
                 """;
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
    string SourceTable);

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