namespace Services.Orchestration;

/// <summary>
///     Queues deployment operations for background processing by the deployment worker.
/// </summary>
public interface IDeploymentJobQueue
{
    /// <summary>
    ///     Enqueues a deployment operation for asynchronous processing.
    /// </summary>
    /// <param name="job">The deployment job to process.</param>
    /// <param name="cancellationToken">Propagates cancellation while waiting for queue capacity.</param>
    ValueTask EnqueueAsync(DeploymentJob job, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads queued deployment jobs until the application is stopping.
    /// </summary>
    /// <param name="cancellationToken">Stops queue consumption during host shutdown.</param>
    /// <returns>An asynchronous stream of queued deployment jobs.</returns>
    IAsyncEnumerable<DeploymentJob> DequeueAllAsync(CancellationToken cancellationToken);
}