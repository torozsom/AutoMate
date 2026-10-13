namespace Domain.Enums;

/// <summary>
///     Durable progress of a SaaS cloud deployment request.
/// </summary>
public enum CloudRunPhase
{
    /// <summary>Admitted and waiting for a launch worker.</summary>
    Queued,

    /// <summary>Preparing verified resources and deployment assets.</summary>
    Preparing,

    /// <summary>Launch preparation finished; waiting to identify the remote workflow.</summary>
    AwaitingWorkflow,

    /// <summary>The matched GitHub Actions workflow is still executing.</summary>
    WorkflowRunning,

    /// <summary>The matched deployment workflow completed successfully.</summary>
    Succeeded,

    /// <summary>Preparation or the matched deployment workflow failed.</summary>
    Failed,

    /// <summary>Workflow monitoring exceeded its configured deadline.</summary>
    TimedOut
}
