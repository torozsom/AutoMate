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

namespace Infrastructure.Tests.Orchestration;

public sealed class DeploymentJobWorkerTests
{
    /// <summary>A slow stop cannot monopolize admission for unrelated projects; the stop lane remains bounded.</summary>
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

    /// <summary>A read that settles after job cancellation must finish before the async iterator is disposed.</summary>
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

    private static LocalDeploymentJob Local(Guid projectId, string name, int port)
    {
        return new LocalDeploymentJob(new DeploymentConfigDto
            { ProjectId = projectId, ProjectName = name, ExposedPort = port });
    }

    private static CloudDeploymentJob Cloud(Guid projectId, string repository)
    {
        return new CloudDeploymentJob(new CloudDeploymentRequestDto
        {
            Config = new DeploymentConfigDto { ProjectId = projectId, ProjectName = repository },
            RepositoryOwner = "owner",
            RepositoryName = repository
        });
    }

    /// <summary>Models an asynchronous channel cancellation completing after the running job has already settled.</summary>
    private sealed class DelayedReadQueue : IDeploymentJobQueue, IAsyncEnumerable<QueuedDeploymentJob>
    {
        private readonly DeploymentJobQueue _inner = new(Options.Create(new DeploymentConcurrencyOptions()));
        public DelayedReader Reader { get; } = new();

        public IAsyncEnumerator<QueuedDeploymentJob> GetAsyncEnumerator(CancellationToken token = default)
        {
            Reader.Token = token;
            return Reader;
        }

        public event Action<Guid>? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        public ValueTask EnqueueAsync(DeploymentJob job, CancellationToken token = default)
        {
            Reader.Job = new QueuedDeploymentJob(job, Stopwatch.GetTimestamp());
            return _inner.EnqueueAsync(job, token);
        }

        public IAsyncEnumerable<QueuedDeploymentJob> DequeueAllAsync(CancellationToken token)
        {
            return this;
        }

        public DeploymentQueueState GetProjectState(Guid project)
        {
            return _inner.GetProjectState(project);
        }

        public void MarkStarted(DeploymentJob job)
        {
            _inner.MarkStarted(job);
        }

        public void MarkCompleted(DeploymentJob job)
        {
            _inner.MarkCompleted(job);
        }
    }

    /// <summary>Rejects disposal during an outstanding MoveNextAsync, like ChannelReader.ReadAllAsync.</summary>
    private sealed class DelayedReader : IAsyncEnumerator<QueuedDeploymentJob>
    {
        private bool _pending;
        private bool _started;
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Settle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public QueuedDeploymentJob Job { get; set; } = null!;
        public CancellationToken Token { get; set; }
        public bool Disposed { get; private set; }
        public QueuedDeploymentJob Current => Job;

        public ValueTask<bool> MoveNextAsync()
        {
            if (!_started)
            {
                _started = true;
                return ValueTask.FromResult(true);
            }

            return new ValueTask<bool>(ReadAsync());
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (_pending) throw new NotSupportedException("Disposal during read");
            return ValueTask.CompletedTask;
        }

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

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

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

        public Tracker Tracker { get; } = new();
        public IDeploymentJobQueue Queue { get; }
        public DeploymentJobWorker Worker { get; }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            await _provider.DisposeAsync();
        }

        public async Task<Start> ReadStartAsync()
        {
            return await Tracker.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task<List<Start>> ReadStartsAsync(int count)
        {
            var starts = new List<Start>();
            for (var index = 0; index < count; index++) starts.Add(await ReadStartAsync());
            return starts;
        }

        public async Task WaitForStateAsync(Guid projectId, Func<DeploymentQueueState, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate(Queue.GetProjectState(projectId)))
                await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record Start(Guid ProjectId, string Kind, Guid ScopeId);

    private sealed class Tracker
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _gates = new();

        /// <summary>Holds stop tasks to model slow Docker/collector cleanup.</summary>
        public bool HoldStops { get; set; }

        public HashSet<Guid> FailProjects { get; } = [];
        public Channel<Start> Started { get; } = Channel.CreateUnbounded<Start>();

        public Task WaitAsync(Guid projectId, CancellationToken cancellationToken)
        {
            return _gates.GetOrAdd(projectId,
                    _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(cancellationToken);
        }

        public void Release(Guid projectId)
        {
            _gates.GetOrAdd(projectId,
                    _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }
    }

    private sealed class FakeLocal(Tracker tracker) : ILocalDeploymentOrchestrator
    {
        private readonly Guid _scopeId = Guid.NewGuid();

        public async Task<Deployment> DeployLocalProjectAsync(DeploymentConfigDto config,
            CancellationToken cancellationToken = default)
        {
            await tracker.Started.Writer.WriteAsync(new Start(config.ProjectId, "local", _scopeId), cancellationToken);
            if (tracker.FailProjects.Contains(config.ProjectId))
                throw new InvalidOperationException("simulated failure");
            await tracker.WaitAsync(config.ProjectId, cancellationToken);
            return new Deployment();
        }

        public async Task StopDeploymentAsync(Guid projectId, string projectName, string csProjectPath,
            CancellationToken cancellationToken = default)
        {
            await tracker.Started.Writer.WriteAsync(new Start(projectId, "stop", _scopeId), cancellationToken);
            if (tracker.HoldStops) await tracker.WaitAsync(projectId, cancellationToken);
        }
    }

    private sealed class FakeCloud(Tracker tracker) : ICloudDeploymentOrchestrator
    {
        private readonly Guid _scopeId = Guid.NewGuid();

        public async Task<Deployment> DeployCloudProjectAsync(CloudDeploymentRequestDto request,
            CancellationToken cancellationToken = default)
        {
            await tracker.Started.Writer.WriteAsync(new Start(request.Config.ProjectId, "cloud", _scopeId),
                cancellationToken);
            await tracker.WaitAsync(request.Config.ProjectId, cancellationToken);
            return new Deployment();
        }
    }

    private sealed class Capabilities : IDeploymentCapabilities
    {
        public bool LocalDeploymentsEnabled => true;
        public bool CloudDeploymentsEnabled => true;
    }
}