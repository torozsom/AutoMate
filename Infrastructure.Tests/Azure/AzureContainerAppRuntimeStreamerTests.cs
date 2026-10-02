using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics;
using Domain.DTO;
using FluentAssertions;
using Infrastructure.Azure;
using Infrastructure.Data;
using Infrastructure.Tests.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Azure;

public sealed class AzureContainerAppRuntimeStreamerTests
{
    [Fact]
    public async Task PollOnce_routes_console_and_system_records_with_deployment_correlation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        var deploymentId = Guid.NewGuid();
        await SeedDeploymentAsync(dbOptions, protector, deploymentId);

        using var serviceProvider = new ServiceCollection()
            .AddScoped<AutoMateDbContext>(_ => new AutoMateDbContext(dbOptions, protector))
            .BuildServiceProvider();
        var diagnostics = new RecordingPublisher();
        var streamer = new AzureContainerAppRuntimeStreamer(diagnostics, new SuccessfulTokenProvider(),
            new StubHttpClientFactory(new DelegateHttpMessageHandler(Respond)),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AzureMonitorLogsOptions { InitialLookback = TimeSpan.FromMinutes(1) }),
            NullLogger<AzureContainerAppRuntimeStreamer>.Instance);

        streamer.StartStreaming(new AzureContainerAppRuntimeStreamRequest
        {
            ProjectId = Guid.NewGuid(),
            DeploymentId = deploymentId,
            UserId = Guid.NewGuid(),
            AzureCredentials = new AzureCloudCredentialsDto { SubscriptionId = "sub", AccessToken = "arm-token" },
            Config = new DeploymentConfigDto { CloudResourceGroupName = "rg", CloudContainerAppName = "app" }
        });

        await streamer.PollOnceAsync(CancellationToken.None);

        diagnostics.Events.Should().Contain(eventItem => eventItem.DeploymentId == deploymentId &&
            eventItem.Message == "console line" &&
            eventItem.TerminalChannel == new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "cloud-web"));
        diagnostics.Events.Should().Contain(eventItem => eventItem.DeploymentId == deploymentId &&
            eventItem.Message == "[Azure system] revision provisioned" &&
            eventItem.TerminalChannel.Kind == DeploymentTerminalChannelKind.System &&
            eventItem.SourceIdentity!.Stream == DeploymentDiagnosticStream.System);
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (request.RequestUri!.Host == "management.azure.com")
        {
            if (request.RequestUri.AbsoluteUri.Contains("/metrics?", StringComparison.Ordinal))
                return DelegateHttpMessageHandler.Json("{ \"value\": [] }");
            return DelegateHttpMessageHandler.Json("{ \"properties\": { \"latestReadyRevisionName\": \"rev-1\" } }");
        }

        var query = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return DelegateHttpMessageHandler.Json(query.Contains("ContainerAppConsoleLogs", StringComparison.Ordinal)
            ? RecordsJson("console line", "stdout", "ContainerAppConsoleLogs")
            : RecordsJson("revision provisioned", "system", "ContainerAppSystemLogs"));
    }

    private static string RecordsJson(string message, string stream, string sourceTable) => $$"""
        { "tables": [{ "columns": [
          { "name": "TimeGenerated" }, { "name": "Message" }, { "name": "ContainerName" },
          { "name": "RevisionName" }, { "name": "Stream" }, { "name": "SourceTable" }],
          "rows": [["2026-09-28T10:00:00Z", "{{message}}", "web", "rev-1", "{{stream}}", "{{sourceTable}}"]] }] }
        """;

    private static async Task SeedDeploymentAsync(DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider protector, Guid deploymentId)
    {
        await using var dbContext = new AutoMateDbContext(options, protector);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.Deployments.Add(new Domain.Entities.Deployment
        {
            Id = deploymentId,
            CsProject = new Domain.Entities.CsProject
            {
                Name = "Web", Path = "Web/Web.csproj",
                Application = new Domain.Entities.Application
                {
                    Name = "Sample", SourceType = Domain.Enums.SourceType.Remote,
                    SourcePathOrUrl = "https://github.com/example/sample",
                    User = new Domain.Entities.LocalUser { Username = "test", Email = "test@example.invalid" }
                }
            }
        });
        await dbContext.SaveChangesAsync();
    }

    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher
    {
        public List<DeploymentDiagnosticEvent> Events { get; } = [];
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SuccessfulTokenProvider : IAzureMonitorLogsTokenProvider
    {
        public Task<AzureMonitorLogsTokenResult> GetTokenAsync(Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureMonitorLogsTokenResult("monitor-token", null));
    }
}
