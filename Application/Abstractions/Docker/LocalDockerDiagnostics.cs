namespace Application.Abstractions.Docker;

/// <summary>Identifies a known project container and its existing terminal tab.</summary>
public sealed record DockerContainerTarget(string Name, string Channel, bool IsDatabase = false);

/// <summary>Non-secret ownership and correlation information for a local deployment collector.</summary>
public sealed record DockerDeploymentTarget(
    Guid ProjectId,
    Guid DeploymentId,
    string ComposeProject,
    IReadOnlyList<DockerContainerTarget> Containers,
    DateTimeOffset RegisteredAt);

/// <summary>Controls host-owned local collectors without exposing Docker SDK types.</summary>
public interface ILocalDeploymentDiagnostics
{
    /// <summary>Registers or replaces a deployment; startup operations collect lifecycle events before Compose runs.</summary>
    Task RegisterAsync(DockerDeploymentTarget target, bool deploymentOperation,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether recovery must register a current deployment.</summary>
    bool IsActive(Guid projectId, Guid deploymentId);

    /// <summary>Enables lifecycle collection for a deployment operation, independently of runtime viewing consent.</summary>
    Task SetOperationAsync(Guid projectId, Guid deploymentId, bool active,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels and awaits all collectors for a project.</summary>
    Task StopProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>Provider boundary for label-filtered daemon events and incremental container output.</summary>
public interface IDockerDiagnosticSource
{
    /// <summary>Monitors daemon events; signals the first subscription attempt so deployment can proceed safely.</summary>
    Task MonitorDaemonAsync(DockerDeploymentTarget target, Action subscribed, CancellationToken cancellationToken);

    /// <summary>Streams a verified project's container with bounded parsing, replay and reconnect handling.</summary>
    Task MonitorContainerAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken);

    /// <summary>Samples a verified container while preserving the independent live/history metric cadence.</summary>
    Task MonitorMetricsAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken);
}