using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Infrastructure.Azure;
using Infrastructure.Tests.TestSupport;

namespace Infrastructure.Tests.Azure;

public sealed class AzureMonitorLogsClientTests
{
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
        sentBody.Should().Contain("ContainerAppConsoleLogs_CL")
            .And.Contain("ContainerAppConsoleLogs");
    }

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
}