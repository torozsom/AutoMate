namespace Application.Abstractions.Diagnostics;

/// <summary>Identifies the adapter that observed a deployment diagnostic.</summary>
public enum DeploymentDiagnosticSource
{
    AutoMate,
    DockerDaemon,
    DockerCompose,
    DockerContainer,
    GitHubActions,
    AzureContainerApps
}

/// <summary>Identifies the stable deployment component that produced an observation.</summary>
public enum DeploymentDiagnosticComponent
{
    Orchestrator,
    Daemon,
    Compose,
    Build,
    Web,
    Database,
    Workflow,
    Job,
    Step,
    Container,
    Revision
}

/// <summary>Identifies the output stream without requiring consumers to parse terminal text.</summary>
public enum DeploymentDiagnosticStream
{
    Control,
    StandardOutput,
    StandardError,
    System,
    Metric
}

/// <summary>Non-secret, typed provider-source identity used for routing and correlation.</summary>
public sealed record DeploymentDiagnosticSourceIdentity(
    DeploymentDiagnosticComponent Component,
    DeploymentDiagnosticStream Stream,
    string? InstanceId = null);

/// <summary>Classifies a diagnostic without requiring consumers to parse message text.</summary>
public enum DeploymentDiagnosticKind
{
    Log,
    Lifecycle,
    BuildProgress,
    WorkflowState,
    JobState,
    StepState,
    Metric,
    Annotation
}

/// <summary>Represents the severity assigned by a normalizing collector.</summary>
public enum DeploymentDiagnosticSeverity
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>Identifies the existing terminal channel to which a safe event may be delivered.</summary>
public enum DeploymentTerminalChannelKind
{
    Build,
    Container,
    Metrics,
    System
}

/// <summary>Describes the target terminal without relying on a display-string convention.</summary>
public sealed record DeploymentTerminalChannel(DeploymentTerminalChannelKind Kind, string? Target = null);

/// <summary>
///     Provider-neutral, versioned deployment observation. Provider payloads must be normalized into this type before
///     crossing into diagnostic processing.
/// </summary>
public sealed record DeploymentDiagnosticEvent(
    Guid ProjectId,
    Guid? DeploymentId,
    DeploymentDiagnosticSource Source,
    DeploymentDiagnosticKind Kind,
    DeploymentDiagnosticSeverity Severity,
    DateTimeOffset TimestampUtc,
    string Message,
    DeploymentTerminalChannel TerminalChannel,
    IReadOnlyDictionary<string, string>? Attributes = null,
    string? TraceId = null,
    string? SpanId = null,
    long? Sequence = null,
    string? Cursor = null,
    DeploymentDiagnosticSourceIdentity? SourceIdentity = null,
    IReadOnlyList<DeploymentMetricSample>? Metrics = null,
    Guid? EventId = null)
{
    /// <summary>Current event schema version.</summary>
    public const int SchemaVersion = 1;
}