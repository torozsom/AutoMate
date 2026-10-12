namespace Application.Abstractions.Diagnostics;

/// <summary>
///     Identifies the adapter that observed a deployment diagnostic.
/// </summary>
public enum DeploymentDiagnosticSource
{
    /// <summary>Observations emitted by AutoMate orchestration.</summary>
    AutoMate,

    /// <summary>Observations emitted by the Docker daemon.</summary>
    DockerDaemon,

    /// <summary>Observations from Docker Compose deployment commands.</summary>
    DockerCompose,

    /// <summary>Observations from an owned Docker container.</summary>
    DockerContainer,

    /// <summary>Observations from GitHub Actions workflows and job logs.</summary>
    GitHubActions,

    /// <summary>Observations from Azure Container Apps and associated Azure diagnostics.</summary>
    AzureContainerApps
}

/// <summary>
///     Identifies the stable deployment component that produced an observation.
/// </summary>
public enum DeploymentDiagnosticComponent
{
    /// <summary>The orchestrator deployment component.</summary>
    Orchestrator,

    /// <summary>The daemon deployment component.</summary>
    Daemon,

    /// <summary>The compose deployment component.</summary>
    Compose,

    /// <summary>The build deployment component.</summary>
    Build,

    /// <summary>The web deployment component.</summary>
    Web,

    /// <summary>The database deployment component.</summary>
    Database,

    /// <summary>The workflow deployment component.</summary>
    Workflow,

    /// <summary>The job deployment component.</summary>
    Job,

    /// <summary>The step deployment component.</summary>
    Step,

    /// <summary>The container deployment component.</summary>
    Container,

    /// <summary>The revision deployment component.</summary>
    Revision
}

/// <summary>
///     Identifies the output stream without requiring consumers to parse terminal text.
/// </summary>
public enum DeploymentDiagnosticStream
{
    /// <summary>The control diagnostic stream.</summary>
    Control,

    /// <summary>The standard output diagnostic stream.</summary>
    StandardOutput,

    /// <summary>The standard error diagnostic stream.</summary>
    StandardError,

    /// <summary>The system diagnostic stream.</summary>
    System,

    /// <summary>The metric diagnostic stream.</summary>
    Metric
}

/// <summary>
///     Non-secret, typed provider-source identity used for routing and correlation.
/// </summary>
/// <param name="Component">The typed deployment component that produced the observation.</param>
/// <param name="Stream">The typed diagnostic output stream.</param>
/// <param name="InstanceId">A bounded non-secret provider instance identity, if recorded.</param>
public sealed record DeploymentDiagnosticSourceIdentity(
    DeploymentDiagnosticComponent Component,
    DeploymentDiagnosticStream Stream,
    string? InstanceId = null);

/// <summary>
///     Classifies a diagnostic without requiring consumers to parse message text.
/// </summary>
public enum DeploymentDiagnosticKind
{
    /// <summary>The log observation category.</summary>
    Log,

    /// <summary>The lifecycle observation category.</summary>
    Lifecycle,

    /// <summary>The build progress observation category.</summary>
    BuildProgress,

    /// <summary>The workflow state observation category.</summary>
    WorkflowState,

    /// <summary>The job state observation category.</summary>
    JobState,

    /// <summary>The step state observation category.</summary>
    StepState,

    /// <summary>The metric observation category.</summary>
    Metric,

    /// <summary>The annotation observation category.</summary>
    Annotation
}

/// <summary>
///     Represents the severity assigned by a normalizing collector.
/// </summary>
public enum DeploymentDiagnosticSeverity
{
    /// <summary>The trace severity level.</summary>
    Trace,

    /// <summary>The debug severity level.</summary>
    Debug,

    /// <summary>The information severity level.</summary>
    Information,

    /// <summary>The warning severity level.</summary>
    Warning,

    /// <summary>The error severity level.</summary>
    Error,

    /// <summary>The critical severity level.</summary>
    Critical
}

/// <summary>
///     Identifies the existing terminal channel to which a safe event may be delivered.
/// </summary>
public enum DeploymentTerminalChannelKind
{
    /// <summary>The build terminal channel.</summary>
    Build,

    /// <summary>The container terminal channel.</summary>
    Container,

    /// <summary>The metrics terminal channel.</summary>
    Metrics,

    /// <summary>The system terminal channel.</summary>
    System
}

/// <summary>
///     Describes the target terminal without relying on a display-string convention.
/// </summary>
/// <param name="Kind">The existing terminal channel category.</param>
/// <param name="Target">The optional terminal target or tab suffix.</param>
public sealed record DeploymentTerminalChannel(DeploymentTerminalChannelKind Kind, string? Target = null);

/// <summary>
///     Provider-neutral, versioned deployment observation. Provider payloads must be normalized
///     into this type before crossing into diagnostic processing.
/// </summary>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="DeploymentId">
///     The deployment identity; null is used only where legacy observations permit it.
/// </param>
/// <param name="Source">The adapter that observed the diagnostic.</param>
/// <param name="Kind">The typed diagnostic observation category.</param>
/// <param name="Severity">The normalized severity used to rank a context group.</param>
/// <param name="TimestampUtc">The original UTC observation timestamp, if recorded.</param>
/// <param name="Message">The message supplied to the operation.</param>
/// <param name="TerminalChannel">The stable terminal routing channel.</param>
/// <param name="Attributes">Optional bounded, non-secret diagnostic attributes.</param>
/// <param name="TraceId">The optional canonical diagnostic trace identifier.</param>
/// <param name="SpanId">The optional diagnostic trace span identifier.</param>
/// <param name="Sequence">The optional provider-local sequence number.</param>
/// <param name="Cursor">The previous read position; an absent cursor selects the initial page.</param>
/// <param name="SourceIdentity">
///     The typed provider component, stream and non-secret instance identity.
/// </param>
/// <param name="Metrics">Recorded numeric samples or aggregate points.</param>
/// <param name="EventId">The stable event identity used to deduplicate retries.</param>
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