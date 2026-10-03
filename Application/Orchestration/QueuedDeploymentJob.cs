namespace Application.Orchestration;

/// <summary>A queued operation and its monotonic enqueue timestamp. Never log the job payload.</summary>
public sealed class QueuedDeploymentJob(DeploymentJob job, long enqueuedTimestamp)
{
    public DeploymentJob Job { get; } = job;
    public long EnqueuedTimestamp { get; } = enqueuedTimestamp;
}

/// <summary>Current in-process state of operations for one project.</summary>
public readonly record struct DeploymentQueueState(int Queued, int Active, int QueuedStops, int ActiveStops)
{
    public int QueuedDeployments => Queued - QueuedStops;
    public int ActiveDeployments => Active - ActiveStops;
}
