namespace Domain.Enums;

/// <summary>Durable progress of a SaaS cloud deployment request.</summary>
public enum CloudRunPhase
{
    Queued,
    Preparing,
    AwaitingWorkflow,
    WorkflowRunning,
    Succeeded,
    Failed,
    TimedOut
}