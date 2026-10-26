using Application.Abstractions.Diagnostics.Events;
using Domain.DTO.Azure;
using Infrastructure.Azure.ContainerApps.Runtime;
using Infrastructure.Azure.Monitor;

namespace Test.Infra.Azure.ContainerApps;

/// <summary>Protects database terminal routing and source-specific availability reporting.</summary>
public sealed class AzureRuntimeDiagnosticPolicyTests
{
    /// <summary>Historical database stdout and stderr use the same terminal identity as live collection.</summary>
    [Theory]
    [InlineData("postgres", "stdout", "azure-db", DeploymentDiagnosticComponent.Database)]
    [InlineData("postgres", "stderr", "azure-db", DeploymentDiagnosticComponent.Database)]
    [InlineData("web", "stdout", "cloud-web", DeploymentDiagnosticComponent.Container)]
    public void Console_history_routes_verified_database_containers(string container, string stream,
        string terminal, DeploymentDiagnosticComponent component)
    {
        var target = Target();
        var record = new AzureMonitorLogRecord(DateTimeOffset.UtcNow, "safe output", container, "rev-1", stream,
            "ContainerAppConsoleLogs");
        var result = AzureRuntimeDiagnosticPolicy.CreateLogEvent(target, AzureContainerAppLogSource.Console, record);

        Assert.Equal(terminal, result.TerminalChannel.Target);
        Assert.Equal(component, result.SourceIdentity!.Component);
        Assert.Equal(
            stream == "stderr" ? DeploymentDiagnosticStream.StandardError : DeploymentDiagnosticStream.StandardOutput,
            result.SourceIdentity.Stream);
    }

    /// <summary>Missing samples and unsupported operational logs stay visible without repeated warnings.</summary>
    [Theory]
    [InlineData("db-owned-metrics-empty")]
    [InlineData("db-owned-logs-empty")]
    [InlineData("db-owned-metrics-only")]
    public async Task Database_availability_is_routed_deduplicated_and_rearmed_on_recovery(string issue)
    {
        var publisher = new Publisher();
        var availability = new AzureRuntimeAvailability(publisher);
        var target = Target();

        await availability.ReportAsync(target, issue, "Fixed guidance.", CancellationToken.None);
        await availability.ReportAsync(target, issue, "Fixed guidance.", CancellationToken.None);

        var observation = Assert.Single(publisher.Events);
        Assert.Equal("azure-db", observation.TerminalChannel.Target);
        Assert.Equal(DeploymentDiagnosticComponent.Database, observation.SourceIdentity!.Component);
        Assert.Equal(DeploymentDiagnosticSeverity.Information, observation.Severity);

        availability.Clear(target, issue);
        await availability.ReportAsync(target, issue, "Fixed guidance.", CancellationToken.None);

        Assert.Equal(2, publisher.Events.Count);
    }

    /// <summary>Creates a verified synthetic target without provider secrets.</summary>
    private static ContainerAppStreamTarget Target()
    {
        return new ContainerAppStreamTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/app", "app",
            new AzureCloudCredentialsDto())
        {
            DatabaseContainers = new HashSet<string>(StringComparer.Ordinal) { "postgres" }
        };
    }

    /// <summary>Records normalized guidance without external delivery.</summary>
    private sealed class Publisher : IDeploymentDiagnosticPublisher
    {
        /// <summary>Observed diagnostic events.</summary>
        public List<DeploymentDiagnosticEvent> Events { get; } = [];

        /// <summary>Accepts fixture observations immediately.</summary>
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);

            return ValueTask.CompletedTask;
        }
    }
}
