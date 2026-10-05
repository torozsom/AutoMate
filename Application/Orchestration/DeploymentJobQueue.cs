using System.Diagnostics;
using System.Threading.Channels;
using Application.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>
///     Channel-backed deployment job queue with bounded capacity to avoid unbounded server memory growth.
/// </summary>
public sealed class DeploymentJobQueue(IOptions<DeploymentConcurrencyOptions> options,
    ILogger<DeploymentJobQueue>? logger = null) : IDeploymentJobQueue
{
    private readonly object _gate = new();
    private readonly int _maxQueued = Math.Clamp(options.Value.MaxQueuedJobs, 1, 1_000);

    private readonly Channel<QueuedDeploymentJob> _queue = Channel.CreateBounded<QueuedDeploymentJob>(
        new BoundedChannelOptions(Math.Clamp(options.Value.MaxQueuedJobs, 1, 1_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Dictionary<Guid, DeploymentQueueState> _states = new();
    private int _queued;

    public event Action<Guid>? StateChanged;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(DeploymentJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        if (job.ProjectId == Guid.Empty) throw new ArgumentException("A deployment job must identify its project.");
        if (job is not (LocalDeploymentJob or CloudDeploymentJob or StopLocalDeploymentJob))
            throw new NotSupportedException($"Unsupported deployment job type {job.GetType().Name}.");
        lock (_gate)
        {
            if (_queued >= _maxQueued)
                throw new InvalidOperationException("The deployment queue is full. Try again after a job starts.");
            var state = GetStateUnderLock(job.ProjectId);
            _states[job.ProjectId] = state with
            {
                Queued = state.Queued + 1,
                QueuedStops = state.QueuedStops + (job is StopLocalDeploymentJob ? 1 : 0)
            };
            _queued++;
            if (!_queue.Writer.TryWrite(new QueuedDeploymentJob(job, Stopwatch.GetTimestamp())))
            {
                _queued--;
                SetStateUnderLock(job.ProjectId, state);
                throw new InvalidOperationException("The deployment queue is full. Try again after a job starts.");
            }

            AutoMateTelemetry.DeploymentJobsQueued.Add(1);
        }

        logger?.LogInformation("Queued {JobType} for project {ProjectId}.", job.GetType().Name, job.ProjectId);
        NotifyStateChanged(job.ProjectId);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<QueuedDeploymentJob> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }

    public DeploymentQueueState GetProjectState(Guid projectId)
    {
        lock (_gate)
        {
            return GetStateUnderLock(projectId);
        }
    }

    public void MarkStarted(DeploymentJob job)
    {
        var projectId = job.ProjectId;
        lock (_gate)
        {
            var state = GetStateUnderLock(projectId);
            SetStateUnderLock(projectId, state with
            {
                Queued = state.Queued - 1,
                Active = state.Active + 1,
                QueuedStops = state.QueuedStops - (job is StopLocalDeploymentJob ? 1 : 0),
                ActiveStops = state.ActiveStops + (job is StopLocalDeploymentJob ? 1 : 0)
            });
            _queued--;
            AutoMateTelemetry.DeploymentJobsQueued.Add(-1);
            AutoMateTelemetry.DeploymentJobsActive.Add(1);
        }

        NotifyStateChanged(projectId);
    }

    public void MarkCompleted(DeploymentJob job)
    {
        var projectId = job.ProjectId;
        lock (_gate)
        {
            var state = GetStateUnderLock(projectId);
            SetStateUnderLock(projectId, state with
            {
                Active = state.Active - 1,
                ActiveStops = state.ActiveStops - (job is StopLocalDeploymentJob ? 1 : 0)
            });
            AutoMateTelemetry.DeploymentJobsActive.Add(-1);
        }

        NotifyStateChanged(projectId);
    }

    private DeploymentQueueState GetStateUnderLock(Guid projectId)
    {
        return _states.TryGetValue(projectId, out var state) ? state : default;
    }

    private void SetStateUnderLock(Guid projectId, DeploymentQueueState state)
    {
        if (state.Queued == 0 && state.Active == 0) _states.Remove(projectId);
        else _states[projectId] = state;
    }

    private void NotifyStateChanged(Guid projectId)
    {
        if (StateChanged is not { } callbacks) return;
        foreach (Action<Guid> callback in callbacks.GetInvocationList())
            try
            {
                callback(projectId);
            }
            catch
            {
                /* A disconnected UI subscriber cannot interrupt job admission. */
            }
    }
}
