using System.Diagnostics;
using System.Threading.Channels;
using Application.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Orchestration;

/// <summary>
///     Channel-backed deployment job queue with bounded capacity to avoid unbounded server
///     memory growth.
/// </summary>
/// <param name="options">The current validated options used by this operation.</param>
/// <param name="logger">The logger used for fixed operational events without diagnostic payloads.</param>
public sealed class DeploymentJobQueue(
    IOptions<DeploymentConcurrencyOptions> options,
    ILogger<DeploymentJobQueue>? logger = null) : IDeploymentJobQueue
{
    /// <summary>Serializes updates to project counters and bounded queue admission.</summary>
    private readonly object _gate = new();

    /// <summary>Clamped maximum number of waiting admitted operations.</summary>
    private readonly int _maxQueued = Math.Clamp(options.Value.MaxQueuedJobs, 1, 1_000);

    /// <summary>Bounded multi-writer channel consumed by the deployment scheduler.</summary>
    private readonly Channel<QueuedDeploymentJob> _queue = Channel.CreateBounded<QueuedDeploymentJob>(
        new BoundedChannelOptions(Math.Clamp(options.Value.MaxQueuedJobs, 1, 1_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Queued and active counters for projects with remaining work.</summary>
    private readonly Dictionary<Guid, DeploymentQueueState> _states = new();

    /// <summary>Total waiting admitted operations; accessed under the state lock.</summary>
    private int _queued;

    /// <summary>Raised after project queue counters change; subscriber failures are isolated.</summary>
    public event Action<Guid>? StateChanged;

    /// <summary>
    ///     Admits a supported deployment operation without waiting when the bounded queue is full.
    /// </summary>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
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

    /// <summary>
    ///     Streams queued operations to the scheduler until consumption ends or is canceled.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>An asynchronous stream of admitted deployment operations.</returns>
    /// <exception cref="OperationCanceledException">Queue consumption is canceled.</exception>
    public IAsyncEnumerable<QueuedDeploymentJob> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    ///     Reads a consistent snapshot of queued and active operations for one project.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>A snapshot of project counters; all counts are zero when no work is registered.</returns>
    public DeploymentQueueState GetProjectState(Guid projectId)
    {
        lock (_gate)
        {
            return GetStateUnderLock(projectId);
        }
    }

    /// <summary>
    ///     Moves an admitted operation from queued to active accounting and notifies subscribers.
    /// </summary>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
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

    /// <summary>
    ///     Removes a finished operation from active accounting and notifies subscribers.
    /// </summary>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
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

    /// <summary>
    ///     Reads project queue counters while the caller holds the state lock.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>The stored project counters, or the default zero-count state.</returns>
    private DeploymentQueueState GetStateUnderLock(Guid projectId)
    {
        return _states.TryGetValue(projectId, out var state) ? state : default;
    }

    /// <summary>
    ///     Stores project counters under the state lock, removing entries with no remaining work.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="state">The project queue-state snapshot or fixed attention state.</param>
    private void SetStateUnderLock(Guid projectId, DeploymentQueueState state)
    {
        if (state.Queued == 0 && state.Active == 0) _states.Remove(projectId);
        else _states[projectId] = state;
    }

    /// <summary>
    ///     Notifies project-state subscribers while isolating disconnected or failing handlers.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
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