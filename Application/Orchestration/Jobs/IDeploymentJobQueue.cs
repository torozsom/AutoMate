namespace Application.Orchestration.Jobs;

/// <summary>
///     Queues deployment operations for background processing by the deployment worker.
/// </summary>
public interface IDeploymentJobQueue
{
    /// <summary>Notifies subscribers that a project's queued or active counts changed.</summary>
    event Action<Guid>? StateChanged;

    /// <summary>
    ///     Enqueues a deployment operation for asynchronous processing.
    /// </summary>
    /// <param name="job">The deployment job to process.</param>
    /// <param name="cancellationToken">Cancels admission before the job enters the queue.</param>
    /// <returns>A completed admission task; a full queue is rejected rather than awaited.</returns>
    /// <exception cref="ArgumentNullException">The deployment job is null.</exception>
    /// <exception cref="ArgumentException">The job does not identify a project.</exception>
    /// <exception cref="NotSupportedException">The deployment job type is not supported.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The bounded queue has no available admission capacity.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    ///     Admission is canceled before the job enters the queue.
    /// </exception>
    ValueTask EnqueueAsync(DeploymentJob job, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads queued deployment jobs until the application is stopping.
    /// </summary>
    /// <param name="cancellationToken">Stops queue consumption during host shutdown.</param>
    /// <returns>An asynchronous stream of queued deployment jobs.</returns>
    /// <exception cref="OperationCanceledException">Queue consumption is canceled.</exception>
    IAsyncEnumerable<QueuedDeploymentJob> DequeueAllAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Reads queued and active operation counters for one project.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>A snapshot of project counters; all counts are zero when no work is registered.</returns>
    DeploymentQueueState GetProjectState(Guid projectId);

    /// <summary>
    ///     Records the transition of an admitted operation from queued to active.
    /// </summary>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
    void MarkStarted(DeploymentJob job);

    /// <summary>
    ///     Records completion of an active operation.
    /// </summary>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
    void MarkCompleted(DeploymentJob job);
}
