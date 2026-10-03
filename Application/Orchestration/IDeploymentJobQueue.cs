namespace Application.Orchestration;

/// <summary>
///     Queues deployment operations for background processing by the deployment worker.
/// </summary>
public interface IDeploymentJobQueue
{
    event Action<Guid>? StateChanged;

    /// <summary>
    ///     Enqueues a deployment operation for asynchronous processing.
    /// </summary>
    /// <param name="job">The deployment job to process.</param>
    /// <param name="cancellationToken">Cancels admission before the job enters the queue.</param>
    /// <exception cref="InvalidOperationException">The bounded queue is full.</exception>
    ValueTask EnqueueAsync(DeploymentJob job, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads queued deployment jobs until the application is stopping.
    /// </summary>
    /// <param name="cancellationToken">Stops queue consumption during host shutdown.</param>
    /// <returns>An asynchronous stream of queued deployment jobs.</returns>
    IAsyncEnumerable<QueuedDeploymentJob> DequeueAllAsync(CancellationToken cancellationToken);

    DeploymentQueueState GetProjectState(Guid projectId);
    void MarkStarted(DeploymentJob job);
    void MarkCompleted(DeploymentJob job);
}
