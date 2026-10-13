using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Application.Abstractions.Hosting;
using Application.Orchestration;
using Domain.DTO;
using Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Test.Infra.Orchestration;

/// <summary>
///     Verifies queued deployment and stop isolation, scope ownership and shutdown behavior.
/// </summary>
/// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
/// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
public sealed class DeploymentJobWorkerTests
{
    /// <summary>
    ///     A slow stop cannot monopolize admission for unrelated projects; the stop lane remains
    ///     bounded.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Independent_stops_progress_while_the_first_stop_is_waiting()
    {
        await using var harness = new Harness();
        harness.Tracker.HoldStops = true;
        var projects = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (project, index) in projects.Select((project, index) => (project, index)))
            await harness.Queue.EnqueueAsync(new StopLocalDeploymentJob(project, $"stop-{index}", "web.csproj"));
        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(4);
        started.Should().OnlyContain(item => item.Kind == "stop");
        harness.Queue.GetProjectState(projects[4]).QueuedStops.Should().Be(1);
        harness.Tracker.Release(projects[1]);
        (await harness.ReadStartAsync()).ProjectId.Should().Be(projects[4]);
        harness.Queue.GetProjectState(projects[0]).ActiveStops.Should().Be(1);
        foreach (var project in projects) harness.Tracker.Release(project);
        foreach (var project in projects)
            await harness.WaitForStateAsync(project, state => state.QueuedStops == 0 && state.ActiveStops == 0);
    }

    /// <summary>
    ///     Verifies that starts two local builds and a cloud job without waiting for completion.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Starts_two_local_builds_and_a_cloud_job_without_waiting_for_completion()
    {
        await using var harness = new Harness();
        var projects = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (project, index) in projects.Select((project, index) => (project, index)))
            await harness.Queue.EnqueueAsync(Local(project, $"app-{index}", 18000 + index));
        var cloud = Guid.NewGuid();
        await harness.Queue.EnqueueAsync(Cloud(cloud, "cloud-app"));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(3);
        started.Count(item => item.Kind == "local").Should().Be(2);
        started.Count(item => item.Kind == "cloud").Should().Be(1);
        harness.Queue.GetProjectState(projects[2]).Queued.Should().Be(1);
        started.Select(item => item.ScopeId).Distinct().Should().HaveCount(3);

        harness.Tracker.Release(started.First(item => item.Kind == "local").ProjectId);
        (await harness.ReadStartAsync()).ProjectId.Should().Be(projects[2]);
    }

    /// <summary>
    ///     Verifies that starts four cloud jobs and admits the fifth when one finishes.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Starts_four_cloud_jobs_and_admits_the_fifth_when_one_finishes()
    {
        await using var harness = new Harness();
        var projects = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (project, index) in projects.Select((project, index) => (project, index)))
            await harness.Queue.EnqueueAsync(Cloud(project, $"repo-{index}"));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(4);
        started.Should().OnlyContain(item => item.Kind == "cloud");
        harness.Queue.GetProjectState(projects[4]).Queued.Should().Be(1);

        harness.Tracker.Release(started[0].ProjectId);
        (await harness.ReadStartAsync()).ProjectId.Should().Be(projects[4]);
    }

    /// <summary>
    ///     Verifies that applies configured lane limits.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Applies_configured_lane_limits()
    {
        await using var harness = new Harness(new DeploymentConcurrencyOptions
        {
            MaxLocalBuilds = 1,
            MaxCloudDeployments = 2
        });
        var locals = Enumerable.Range(1, 2).Select(_ => Guid.NewGuid()).ToArray();
        var clouds = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < locals.Length; index++)
            await harness.Queue.EnqueueAsync(Local(locals[index], $"local-{index}", 18600 + index));
        for (var index = 0; index < clouds.Length; index++)
            await harness.Queue.EnqueueAsync(Cloud(clouds[index], $"cloud-{index}"));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(3);
        started.Count(item => item.Kind == "local").Should().Be(1);
        started.Count(item => item.Kind == "cloud").Should().Be(2);
        harness.Queue.GetProjectState(locals[1]).Queued.Should().Be(1);
        harness.Queue.GetProjectState(clouds[2]).Queued.Should().Be(1);
    }

    /// <summary>
    ///     Verifies that self hosted unlimited setting admits more than the previous local ceiling.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Self_hosted_unlimited_setting_admits_more_than_the_previous_local_ceiling()
    {
        await using var harness = new Harness(new DeploymentConcurrencyOptions { MaxLocalBuilds = 0 });
        var projects = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < projects.Length; index++)
            await harness.Queue.EnqueueAsync(Local(projects[index], $"unlimited-{index}", 18700 + index));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(projects.Length);
        started.Select(item => item.ProjectId).Should().BeEquivalentTo(projects);
        started.Should().OnlyContain(item => item.Kind == "local");
    }

    /// <summary>
    ///     Verifies that preserves project order and does not use a build slot while waiting.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Preserves_project_order_and_does_not_use_a_build_slot_while_waiting()
    {
        await using var harness = new Harness();
        var first = Guid.NewGuid();
        var other = Guid.NewGuid();
        await harness.Queue.EnqueueAsync(Local(first, "first", 18100));
        await harness.Queue.EnqueueAsync(new StopLocalDeploymentJob(first, "first", "first.csproj"));
        await harness.Queue.EnqueueAsync(Local(other, "other", 18101));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(2);
        started.Select(item => item.ProjectId).Should().BeEquivalentTo([first, other]);
        harness.Queue.GetProjectState(first).Queued.Should().Be(1);
        harness.Tracker.Release(first);
        var stop = await harness.ReadStartAsync();
        stop.ProjectId.Should().Be(first);
        stop.Kind.Should().Be("stop");
    }

    /// <summary>
    ///     Verifies that keeps conflicting ports queued without blocking an unrelated build.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Keeps_conflicting_ports_queued_without_blocking_an_unrelated_build()
    {
        await using var harness = new Harness();
        var first = Guid.NewGuid();
        var conflict = Guid.NewGuid();
        var independent = Guid.NewGuid();
        await harness.Queue.EnqueueAsync(Local(first, "first", 18200));
        await harness.Queue.EnqueueAsync(Local(conflict, "conflict", 18200));
        await harness.Queue.EnqueueAsync(Local(independent, "independent", 18201));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(2);
        started.Select(item => item.ProjectId).Should().BeEquivalentTo([first, independent]);
        harness.Queue.GetProjectState(conflict).Queued.Should().Be(1);
        harness.Tracker.Release(first);
        (await harness.ReadStartAsync()).ProjectId.Should().Be(conflict);
    }

    /// <summary>
    ///     Verifies that serializes conflicting cloud branches without blocking other repositories.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Serializes_conflicting_cloud_branches_without_blocking_other_repositories()
    {
        await using var harness = new Harness();
        var first = Guid.NewGuid();
        var conflict = Guid.NewGuid();
        var independent = Guid.NewGuid();
        await harness.Queue.EnqueueAsync(Cloud(first, "shared"));
        await harness.Queue.EnqueueAsync(Cloud(conflict, "shared"));
        await harness.Queue.EnqueueAsync(Cloud(independent, "other"));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(2);
        started.Select(item => item.ProjectId).Should().BeEquivalentTo([first, independent]);
        harness.Queue.GetProjectState(conflict).Queued.Should().Be(1);
        harness.Tracker.Release(first);
        (await harness.ReadStartAsync()).ProjectId.Should().Be(conflict);
    }

    /// <summary>
    ///     Verifies that rejects jobs beyond the configured queue bound.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Rejects_jobs_beyond_the_configured_queue_bound()
    {
        var queue = new DeploymentJobQueue(Options.Create(new DeploymentConcurrencyOptions { MaxQueuedJobs = 2 }));
        await queue.EnqueueAsync(Local(Guid.NewGuid(), "one", 18300));
        await queue.EnqueueAsync(Local(Guid.NewGuid(), "two", 18301));
        var enqueue = async () => await queue.EnqueueAsync(Local(Guid.NewGuid(), "three", 18302));
        await enqueue.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*queue is full*");
    }

    /// <summary>
    ///     Verifies that a failed job releases its slot and does not stop other projects.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task A_failed_job_releases_its_slot_and_does_not_stop_other_projects()
    {
        await using var harness = new Harness();
        var failed = Guid.NewGuid();
        var other = Guid.NewGuid();
        harness.Tracker.FailProjects.Add(failed);
        await harness.Queue.EnqueueAsync(Local(failed, "broken", 18400));
        await harness.Queue.EnqueueAsync(Local(other, "healthy", 18401));

        await harness.Worker.StartAsync(CancellationToken.None);
        var started = await harness.ReadStartsAsync(2);
        started.Select(item => item.ProjectId).Should().BeEquivalentTo([failed, other]);
        await harness.WaitForStateAsync(failed, state => state.Active == 0);
        harness.Queue.GetProjectState(other).Active.Should().Be(1);
        harness.Tracker.Release(other);
        await harness.WaitForStateAsync(other, state => state.Active == 0);
    }

    /// <summary>
    ///     Verifies that shutdown cancels admitted jobs and releases active state.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Shutdown_cancels_admitted_jobs_and_releases_active_state()
    {
        await using var harness = new Harness();
        var project = Guid.NewGuid();
        await harness.Queue.EnqueueAsync(Local(project, "cancelled", 18500));
        await harness.Worker.StartAsync(CancellationToken.None);
        await harness.ReadStartAsync();

        await harness.Worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        harness.Queue.GetProjectState(project).Active.Should().Be(0);
        await harness.Worker.ExecuteTask!;
        harness.Worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    /// <summary>
    ///     A read that settles after job cancellation must finish before the async iterator is
    ///     disposed.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Shutdown_waits_for_pending_queue_read_before_disposing_iterator()
    {
        var queue = new DelayedReadQueue();
        await using var harness = new Harness(queue: queue);
        var project = Guid.NewGuid();
        await queue.EnqueueAsync(Local(project, "shutdown", 18510));
        await harness.Worker.StartAsync(default);
        await harness.ReadStartAsync();
        await queue.Reader.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopping = harness.Worker.StopAsync(timeout.Token);
        try
        {
            await queue.Reader.Cancelled.Task.WaitAsync(timeout.Token);
            await harness.WaitForStateAsync(project, state => state.Active == 0);
            queue.Reader.Disposed.Should().BeFalse();
            stopping.IsCompleted.Should().BeFalse();
        }
        finally
        {
            queue.Reader.Settle.TrySetResult();
        }

        await stopping;
        await harness.Worker.ExecuteTask!;
        queue.Reader.Disposed.Should().BeTrue();
        harness.Worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    /// <summary>
    ///     Creates local data with the supplied identities and synthetic scenario values.
    /// </summary>
    /// <param name="projectId">Synthetic project identity.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <param name="port">Local host-port number under test.</param>
    /// <returns>The configured local deployment job instance or response for this scenario.</returns>
    private static LocalDeploymentJob Local(Guid projectId, string name, int port)
    {
        return new LocalDeploymentJob(new DeploymentConfigDto
            { ProjectId = projectId, ProjectName = name, ExposedPort = port });
    }

    /// <summary>
    ///     Creates cloud data with the supplied identities and synthetic scenario values.
    /// </summary>
    /// <param name="projectId">Synthetic project identity.</param>
    /// <param name="repository">The GitHub repository name.</param>
    /// <returns>The configured cloud deployment job instance or response for this scenario.</returns>
    private static CloudDeploymentJob Cloud(Guid projectId, string repository)
    {
        return new CloudDeploymentJob(new CloudDeploymentRequestDto
        {
            Config = new DeploymentConfigDto { ProjectId = projectId, ProjectName = repository },
            RepositoryOwner = "owner",
            RepositoryName = repository
        });
    }

    /// <summary>
    ///     Models an asynchronous channel cancellation completing after the running job has already
    ///     settled.
    /// </summary>
    private sealed class DelayedReadQueue : IDeploymentJobQueue, IAsyncEnumerable<QueuedDeploymentJob>
    {
        /// <summary>Underlying queue retaining admission and per-project state.</summary>
        private readonly DeploymentJobQueue _inner = new(Options.Create(new DeploymentConcurrencyOptions()));

        /// <summary>Controlled asynchronous queue reader.</summary>
        public DelayedReader Reader { get; } = new();

        /// <summary>
        ///     Captures cancellation and returns the controlled queue reader.
        /// </summary>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>The controlled reader with the supplied cancellation token recorded.</returns>
        public IAsyncEnumerator<QueuedDeploymentJob> GetAsyncEnumerator(CancellationToken token = default)
        {
            Reader.Token = token;
            return Reader;
        }

        /// <summary>Queue-state notifications forwarded by the wrapper.</summary>
        public event Action<Guid>? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        /// <summary>
        ///     Captures the queued job and forwards admission to the underlying queue.
        /// </summary>
        /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public ValueTask EnqueueAsync(DeploymentJob job, CancellationToken token = default)
        {
            Reader.Job = new QueuedDeploymentJob(job, Stopwatch.GetTimestamp());
            return _inner.EnqueueAsync(job, token);
        }

        /// <summary>
        ///     Exposes this fixture as the controlled asynchronous queue stream.
        /// </summary>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This controlled asynchronous fixture stream.</returns>
        public IAsyncEnumerable<QueuedDeploymentJob> DequeueAllAsync(CancellationToken token)
        {
            return this;
        }

        /// <summary>
        ///     Forwards get project state to the underlying fixture adapter.
        /// </summary>
        /// <param name="project">Synthetic project identity.</param>
        /// <returns>The configured deployment queue state instance or response for this scenario.</returns>
        public DeploymentQueueState GetProjectState(Guid project)
        {
            return _inner.GetProjectState(project);
        }

        /// <summary>
        ///     Forwards mark started to the underlying fixture adapter.
        /// </summary>
        /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
        public void MarkStarted(DeploymentJob job)
        {
            _inner.MarkStarted(job);
        }

        /// <summary>
        ///     Forwards mark completed to the underlying fixture adapter.
        /// </summary>
        /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
        public void MarkCompleted(DeploymentJob job)
        {
            _inner.MarkCompleted(job);
        }
    }

    /// <summary>
    ///     Rejects disposal during an outstanding MoveNextAsync, like ChannelReader.ReadAllAsync.
    /// </summary>
    private sealed class DelayedReader : IAsyncEnumerator<QueuedDeploymentJob>
    {
        /// <summary>Whether the controlled asynchronous read is outstanding.</summary>
        private bool _pending;

        /// <summary>Whether the first job has been yielded.</summary>
        private bool _started;

        /// <summary>Signal completed when a controlled read starts waiting.</summary>
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signal completed when the read token is canceled.</summary>
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gate allowing the pending read to finish after cancellation.</summary>
        public TaskCompletionSource Settle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Synthetic queued job yielded by the reader.</summary>
        public QueuedDeploymentJob Job { get; set; } = null!;

        /// <summary>Cancellation token captured by the reader.</summary>
        public CancellationToken Token { get; set; }

        /// <summary>Whether fixture resource disposal has been observed.</summary>
        public bool Disposed { get; private set; }

        /// <summary>Current synthetic queued job.</summary>
        public QueuedDeploymentJob Current => Job;

        /// <summary>
        ///     Yields the first queued job, then starts a controlled cancellation-aware pending read.
        /// </summary>
        /// <returns>A task returning the configured fixture condition or completion outcome.</returns>
        public ValueTask<bool> MoveNextAsync()
        {
            if (!_started)
            {
                _started = true;
                return ValueTask.FromResult(true);
            }

            return new ValueTask<bool>(ReadAsync());
        }

        /// <summary>
        ///     Records disposal and rejects attempts made while the controlled read is outstanding.
        /// </summary>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (_pending) throw new NotSupportedException("Disposal during read");
            return ValueTask.CompletedTask;
        }

        /// <summary>
        ///     Waits for cancellation and explicit settlement before completing the pending read.
        /// </summary>
        /// <returns>A task returning false for every call in this fixture.</returns>
        private async Task<bool> ReadAsync()
        {
            _pending = true;
            Pending.TrySetResult();
            using var registration = Token.Register(() => Cancelled.TrySetResult());
            await Cancelled.Task;
            await Settle.Task;
            _pending = false;
            Token.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>
    ///     Owns the queue, controlled orchestrators, tracker and deployment worker used by scheduling
    ///     scenarios.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
    /// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>Owned dependency provider for the scheduling fixture.</summary>
        private readonly ServiceProvider _provider;

        /// <summary>
        ///     Initializes the harness fixture and its required test dependencies.
        /// </summary>
        /// <param name="options">Optional worker admission and concurrency settings.</param>
        /// <param name="queue">The bounded deployment-operation queue consumed by the worker.</param>
        public Harness(DeploymentConcurrencyOptions? options = null, IDeploymentJobQueue? queue = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(Tracker);
            services.AddScoped<ILocalDeploymentOrchestrator, FakeLocal>();
            services.AddScoped<ICloudDeploymentOrchestrator, FakeCloud>();
            services.AddSingleton<IDeploymentCapabilities>(new Capabilities());
            services.AddSingleton<IDeploymentStatusNotifier>(new DeploymentStatusNotifier(
                NullLogger<DeploymentStatusNotifier>.Instance));
            services.AddSingleton(Options.Create(
                options ?? new DeploymentConcurrencyOptions { MaxLocalBuilds = 2, MaxCloudDeployments = 4 }));
            if (queue is null) services.AddSingleton<IDeploymentJobQueue, DeploymentJobQueue>();
            else services.AddSingleton(queue);
            services.AddSingleton<ILogger<DeploymentJobWorker>>(NullLogger<DeploymentJobWorker>.Instance);
            services.AddSingleton<DeploymentJobWorker>();
            _provider = services.BuildServiceProvider();
            Queue = _provider.GetRequiredService<IDeploymentJobQueue>();
            Worker = _provider.GetRequiredService<DeploymentJobWorker>();
        }

        /// <summary>Shared operation observations and gates.</summary>
        public Tracker Tracker { get; } = new();

        /// <summary>Queue used by the scheduling fixture.</summary>
        public IDeploymentJobQueue Queue { get; }

        /// <summary>Owned deployment worker under test.</summary>
        public DeploymentJobWorker Worker { get; }

        /// <summary>
        ///     Releases owned fixture services, connections and background resources.
        /// </summary>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            await _provider.DisposeAsync();
        }

        /// <summary>
        ///     Waits up to five seconds for the next recorded operation start.
        /// </summary>
        /// <returns>A task returning the configured start instance or response for this scenario.</returns>
        /// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
        public async Task<Start> ReadStartAsync()
        {
            return await Tracker.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        ///     Reads the requested number of operation starts through bounded fixture waits.
        /// </summary>
        /// <param name="count">Recorded deployment total used to size the bar.</param>
        /// <returns>A task returning the configured synthetic collection or recorded fixture selection.</returns>
        public async Task<List<Start>> ReadStartsAsync(int count)
        {
            var starts = new List<Start>();
            for (var index = 0; index < count; index++) starts.Add(await ReadStartAsync());
            return starts;
        }

        /// <summary>
        ///     Polls queue state until the predicate holds or the five-second fixture deadline cancels the
        ///     wait.
        /// </summary>
        /// <param name="projectId">Synthetic project identity.</param>
        /// <param name="predicate">Queue-state condition required before the wait completes.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public async Task WaitForStateAsync(Guid projectId, Func<DeploymentQueueState, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate(Queue.GetProjectState(projectId)))
                await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>
    ///     Records a synthetic operation start with project, operation kind and scoped orchestrator
    ///     identity.
    /// </summary>
    /// <param name="ProjectId">Synthetic project identity.</param>
    /// <param name="Kind">Synthetic operation or response kind.</param>
    /// <param name="ScopeId">Scoped orchestrator identity captured by the fixture.</param>
    private sealed record Start(Guid ProjectId, string Kind, Guid ScopeId);

    /// <summary>
    ///     Records operation starts and controls per-project completion and failure gates.
    /// </summary>
    private sealed class Tracker
    {
        /// <summary>Per-project completion gates.</summary>
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _gates = new();

        /// <summary>Holds stop tasks to model slow Docker/collector cleanup.</summary>
        public bool HoldStops { get; set; }

        /// <summary>Projects configured to fail local admission.</summary>
        public HashSet<Guid> FailProjects { get; } = [];

        /// <summary>Signal or channel recording controlled operation starts.</summary>
        public Channel<Start> Started { get; } = Channel.CreateUnbounded<Start>();

        /// <summary>
        ///     Waits on the selected project completion gate with caller cancellation.
        /// </summary>
        /// <param name="projectId">Synthetic project identity.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public Task WaitAsync(Guid projectId, CancellationToken cancellationToken)
        {
            return _gates.GetOrAdd(projectId,
                    _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        ///     Completes the selected project gate, creating it when necessary.
        /// </summary>
        /// <param name="projectId">Synthetic project identity.</param>
        public void Release(Guid projectId)
        {
            _gates.GetOrAdd(projectId,
                    _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }
    }

    /// <summary>
    ///     Records scoped local deployment/stop operations and waits on fixture-controlled gates.
    /// </summary>
    /// <param name="tracker">Shared fixture operation tracker and completion gates.</param>
    private sealed class FakeLocal(Tracker tracker) : ILocalDeploymentOrchestrator
    {
        /// <summary>Identity of this scoped synthetic orchestrator.</summary>
        private readonly Guid _scopeId = Guid.NewGuid();

        /// <summary>
        ///     Records local admission, injects configured project failure and waits for release before
        ///     returning a deployment.
        /// </summary>
        /// <param name="config">The deployment configuration used by the operation.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the configured deployment instance or response for this scenario.</returns>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public async Task<Deployment> DeployLocalProjectAsync(DeploymentConfigDto config,
            CancellationToken cancellationToken = default)
        {
            await tracker.Started.Writer.WriteAsync(new Start(config.ProjectId, "local", _scopeId), cancellationToken);
            if (tracker.FailProjects.Contains(config.ProjectId))
                throw new InvalidOperationException("simulated failure");
            await tracker.WaitAsync(config.ProjectId, cancellationToken);
            return new Deployment();
        }

        /// <summary>
        ///     Records stop admission and waits for release only when the fixture holds stops.
        /// </summary>
        /// <param name="projectId">Synthetic project identity.</param>
        /// <param name="projectName">Project name used to derive resource defaults; ignored by this fixture.</param>
        /// <param name="csProjectPath">The local C# project path used to locate Compose resources; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public async Task StopDeploymentAsync(Guid projectId, string projectName, string csProjectPath,
            CancellationToken cancellationToken = default)
        {
            await tracker.Started.Writer.WriteAsync(new Start(projectId, "stop", _scopeId), cancellationToken);
            if (tracker.HoldStops) await tracker.WaitAsync(projectId, cancellationToken);
        }
    }

    /// <summary>
    ///     Records scoped cloud deployment operations and waits on fixture-controlled gates.
    /// </summary>
    /// <param name="tracker">Shared fixture operation tracker and completion gates.</param>
    private sealed class FakeCloud(Tracker tracker) : ICloudDeploymentOrchestrator
    {
        /// <summary>Identity of this scoped synthetic orchestrator.</summary>
        private readonly Guid _scopeId = Guid.NewGuid();

        /// <summary>
        ///     Records cloud admission and waits for release before returning a synthetic deployment.
        /// </summary>
        /// <param name="request">Synthetic cloud deployment request.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the configured deployment instance or response for this scenario.</returns>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public async Task<Deployment> DeployCloudProjectAsync(CloudDeploymentRequestDto request,
            CancellationToken cancellationToken = default)
        {
            await tracker.Started.Writer.WriteAsync(new Start(request.Config.ProjectId, "cloud", _scopeId),
                cancellationToken);
            await tracker.WaitAsync(request.Config.ProjectId, cancellationToken);
            return new Deployment();
        }
    }

    /// <summary>
    ///     Enables both deployment capabilities for scheduling fixtures.
    /// </summary>
    private sealed class Capabilities : IDeploymentCapabilities
    {
        /// <summary>Whether fixture capabilities permit local deployment.</summary>
        public bool LocalDeploymentsEnabled => true;

        /// <summary>Whether fixture capabilities permit cloud deployment.</summary>
        public bool CloudDeploymentsEnabled => true;
    }
}