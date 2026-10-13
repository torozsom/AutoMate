using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Infrastructure.Azure;
using Test.Infra.TestSupport;

namespace Test.Infra.Azure;

/// <summary>
///     Verifies Azure diagnostic query scoping, parsing and ingestion-cursor behavior using
///     synthetic HTTP responses.
/// </summary>
public sealed class AzureMonitorLogsClientTests
{
    /// <summary>
    ///     Native event time remains distinct from the ordered ingestion cursor for delayed arrivals.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Query_preserves_delayed_event_time_and_equal_ingestion_hash_cursor()
    {
        var ingestion = DateTimeOffset.Parse("2026-10-10T11:00:00Z");
        var client = new AzureMonitorLogsClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(request =>
        {
            var query = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement
                .GetProperty("query").GetString()!;
            Assert.Contains("IngestedAt > datetime", query);
            Assert.Contains("strcmp(CursorHash, 'hash-a') > 0", query);
            Assert.Contains("order by IngestedAt asc, CursorHash asc", query);
            Assert.Contains("hash_sha256(strcat(tostring(TimeGenerated)", query);
            Assert.DoesNotContain("format_datetime", query);
            Assert.Contains("1microsecond", query);
            Assert.Contains("EnvironmentName_s", query);
            Assert.Contains("'verified-env'", query);
            return DelegateHttpMessageHandler.Json("""
                                                   {"tables":[{"columns":[{"name":"TimeGenerated"},{"name":"IngestedAt"},{"name":"Message"},{"name":"CursorHash"}],"rows":[["2026-10-10T09:00:00Z","2026-10-10T11:00:00Z","delayed","hash-b"]]}]}
                                                   """);
        })));
        var result = await client.QueryAsync("/subscriptions/sub/providers/Microsoft.App/managedEnvironments/env",
            "token",
            AzureContainerAppLogSource.Console, "app", ingestion.AddMinutes(-1), 10, default, "rev", ingestion,
            "hash-a", "verified-env", "arm-env");
        var record = Assert.Single(result.Records);
        Assert.Equal(ingestion.AddHours(-2), record.TimestampUtc);
        Assert.Equal(ingestion, record.IngestionTimestampUtc);
        Assert.Equal("hash-b", record.CursorHash);
    }

    /// <summary>
    ///     Verifies that query normalizes records and uses resource scoped endpoint.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Query_normalizes_records_and_uses_resource_scoped_endpoint()
    {
        HttpRequestMessage? sentRequest = null;
        string? sentBody = null;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            sentRequest = request;
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return DelegateHttpMessageHandler.Json("""
                                                   { "tables": [{ "columns": [
                                                     { "name": "TimeGenerated" }, { "name": "Message" }, { "name": "ContainerName" },
                                                     { "name": "RevisionName" }, { "name": "Stream" }, { "name": "SourceTable" }],
                                                     "rows": [["2026-09-28T10:00:00Z", "hello", "web", "rev-1", "stdout", "ContainerAppConsoleLogs"]] }] }
                                                   """);
        });
        var client = new AzureMonitorLogsClient(new StubHttpClientFactory(handler));

        var result = await client.QueryAsync(
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/app", "monitor-token",
            AzureContainerAppLogSource.Console, "app", DateTimeOffset.Parse("2026-09-28T09:00:00Z"), 10,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Records.Should().ContainSingle().Which.Should().BeEquivalentTo(new AzureMonitorLogRecord(
            DateTimeOffset.Parse("2026-09-28T10:00:00Z"), "hello", "web", "rev-1", "stdout",
            "ContainerAppConsoleLogs"));
        sentRequest!.RequestUri!.AbsoluteUri.Should().Be(
            "https://api.loganalytics.azure.com/v1/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/app/query");
        sentRequest.Headers.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "monitor-token"));
        sentBody.Should().Contain("ContainerAppConsoleLogs*")
            .And.Contain("ContainerAppName_s");
    }

    /// <summary>
    ///     Verifies that query surfaces a safe permission failure.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Query_surfaces_a_safe_permission_failure()
    {
        var handler = new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new AzureMonitorLogsClient(new StubHttpClientFactory(handler));

        var result = await client.QueryAsync("/subscriptions/sub/providers/Microsoft.App/containerApps/app", "token",
            AzureContainerAppLogSource.System, "app", DateTimeOffset.UtcNow, 10, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Contain("Log Analytics Reader").And.NotContain("token");
    }

    /// <summary>
    ///     Partial Azure results cannot silently lose records by committing a cursor.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Query_rejects_partial_success_without_exposing_provider_details()
    {
        var client = new AzureMonitorLogsClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(_ =>
            DelegateHttpMessageHandler.Json("""{"tables": [], "error": {"message": "secret-provider-detail"}}"""))));
        var result = await client.QueryAsync("/subscriptions/sub/providers/Microsoft.App/managedEnvironments/env",
            "token", AzureContainerAppLogSource.Console, "app", DateTimeOffset.UtcNow, 10, default);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Records);
        Assert.DoesNotContain("secret-provider-detail", result.FailureReason);
    }
}