namespace Application.Orchestration.Jobs;

/// <summary>
///     A queued operation and its monotonic enqueue timestamp. Never log the job payload.
/// </summary>
/// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
/// <param name="enqueuedTimestamp">
///     The monotonic timestamp captured when the operation entered the queue.
/// </param>
public sealed class QueuedDeploymentJob(DeploymentJob job, long enqueuedTimestamp)
{
    /// <summary>The admitted operation; its credential-bearing payload must not be logged.</summary>
    public DeploymentJob Job { get; } = job;

    /// <summary>Monotonic enqueue timestamp used to measure waiting duration.</summary>
    public long EnqueuedTimestamp { get; } = enqueuedTimestamp;
}

/// <summary>
///     Current in-process state of operations for one project.
/// </summary>
/// <param name="Queued">The current queued-operation count.</param>
/// <param name="Active">The current active-operation count.</param>
/// <param name="QueuedStops">The current count of queued local-stop operations.</param>
/// <param name="ActiveStops">The current active local-stop count.</param>
public readonly record struct DeploymentQueueState(int Queued, int Active, int QueuedStops, int ActiveStops)
{
    /// <summary>Queued build operations, excluding queued local stops.</summary>
    public int QueuedDeployments => Queued - QueuedStops;

    /// <summary>Active build operations, excluding active local stops.</summary>
    public int ActiveDeployments => Active - ActiveStops;
}
