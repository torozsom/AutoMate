using System.Threading.Channels;

namespace Services.Orchestration;

/// <summary>
///     Channel-backed deployment job queue with bounded capacity to avoid unbounded server memory growth.
/// </summary>
public sealed class DeploymentJobQueue : IDeploymentJobQueue
{
    /// <summary>
    ///     Maximum number of deployment jobs waiting for background processing.
    /// </summary>
    private const int QueueCapacity = 100;

    private readonly Channel<DeploymentJob> _queue = Channel.CreateBounded<DeploymentJob>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(DeploymentJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await _queue.Writer.WriteAsync(job, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<DeploymentJob> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
}