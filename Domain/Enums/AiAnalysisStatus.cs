namespace Domain.Enums;

/// <summary>Lifecycle state of an asynchronous AI deployment analysis; existing persisted ordinals remain stable.</summary>
public enum AiAnalysisStatus
{
    /// <summary>Waiting for initial processing or a scheduled retry.</summary>
    Queued = 0,

    /// <summary>Currently processing under a renewable lease.</summary>
    Running = 1,

    /// <summary>A validated result has been published.</summary>
    Completed = 2,

    /// <summary>Processing ended with a safe terminal failure.</summary>
    Failed = 3,

    /// <summary>Processing could not run under the current policy or available context.</summary>
    Skipped = 4,

    /// <summary>The owner canceled processing; no later result or retry may be published.</summary>
    Cancelled = 5
}