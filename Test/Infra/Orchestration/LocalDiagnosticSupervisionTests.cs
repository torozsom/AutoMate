using System.Collections.Concurrent;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Orchestration;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Infra.Orchestration;

/// <summary>
///     Verifies host ownership, replacement and consent using real isolated SQLite policy reads.
/// </summary>
/// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
public sealed class LocalDiagnosticSupervisionTests
{
    /// <summary>
    ///     Slow source cleanup retains same-project ownership without holding the shared registry gate.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Slow_project_shutdown_does_not_block_another_project_or_overlap_its_replacement()
    {
        await using var harness = await Harness.CreateAsync(false);
        harness.Source.CleanupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.Manager.RegisterAsync(harness.Target, true);
        var stop = harness.Manager.StopProjectAsync(harness.Target.ProjectId);
        await harness.Source.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = harness.Manager.RegisterAsync(harness.Target, true);
        try
        {
            var other = harness.Target with { ProjectId = Guid.NewGuid(), DeploymentId = Guid.NewGuid() };
            await harness.Manager.RegisterAsync(other, true).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stop.IsCompleted);
            Assert.False(replacement.IsCompleted);
            Assert.Contains(harness.Source.Subscriptions, s => s.Kind == "daemon");
        }
        finally
        {
            harness.Source.CleanupGate.TrySetResult();
        }

        await Task.WhenAll(stop, replacement).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(harness.Manager.IsActive(harness.Target.ProjectId, harness.Target.DeploymentId));
        Assert.Contains(harness.Source.Subscriptions, s => s.Kind == "daemon" && !s.Stopped.Task.IsCompleted);
    }

    /// <summary>
    ///     Lifecycle and runtime collection start without viewers or legacy opt-in and stop with the
    ///     host.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Operation_starts_daemon_before_return_and_host_shutdown_awaits_it()
    {
        await using var harness = await Harness.CreateAsync(false);
        await harness.Manager.RegisterAsync(harness.Target, true);
        await harness.Source.RuntimeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(harness.Source.Subscriptions, s => s.Kind == "daemon");
        Assert.Contains(harness.Source.Subscriptions, s => s.Kind == "logs");
        Assert.Contains(harness.Source.Subscriptions, s => s.Kind == "metrics");
        await harness.Manager.StopAsync(CancellationToken.None);
        Assert.All(harness.Source.Subscriptions, s => Assert.True(s.Stopped.Task.IsCompletedSuccessfully));
        Assert.False(harness.Manager.IsActive(harness.Target.ProjectId, harness.Target.DeploymentId));
    }

    /// <summary>
    ///     Replacement awaits cancellation acknowledgements and cannot remove its new registry entry.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Replacement_awaits_old_sources_and_keeps_new_target_active()
    {
        await using var harness = await Harness.CreateAsync(false);
        await harness.Manager.RegisterAsync(harness.Target, true);
        await harness.Source.RuntimeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var first = harness.Source.Subscriptions.ToArray();
        var next = harness.Target with { DeploymentId = Guid.NewGuid(), RegisteredAt = DateTimeOffset.UtcNow };
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var previous = await db.Deployments.SingleAsync();
            db.Deployments.Add(new Deployment
            {
                Id = next.DeploymentId,
                CsProjectId = previous.CsProjectId,
                Status = DeploymentStatus.Starting,
                CreatedAt = previous.CreatedAt.AddSeconds(1)
            });
            await db.SaveChangesAsync();
        }

        await harness.Manager.RegisterAsync(next, true);
        Assert.All(first, source => Assert.True(source.Stopped.Task.IsCompletedSuccessfully));
        Assert.True(harness.Manager.IsActive(next.ProjectId, next.DeploymentId));
        Assert.False(harness.Manager.IsActive(next.ProjectId, harness.Target.DeploymentId));
        await harness.Manager.StopProjectAsync(next.ProjectId);
        Assert.All(harness.Source.Subscriptions, s => Assert.True(s.Stopped.Task.IsCompletedSuccessfully));
        Assert.False(harness.Manager.IsActive(next.ProjectId, next.DeploymentId));
    }

    /// <summary>
    ///     Legacy disabling cannot stop automatic collection; explicit project shutdown still cancels
    ///     all sources.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Legacy_flag_cannot_disable_runtime_collection()
    {
        await using var harness = await Harness.CreateAsync(true);
        await harness.Manager.RegisterAsync(harness.Target, false);
        await harness.Source.RuntimeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(harness.Source.Subscriptions, s => s.Kind == "logs");
        Assert.Contains(harness.Source.Subscriptions, s => s.Kind == "metrics");
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            (await db.Applications.SingleAsync()).RuntimeDiagnosticsEnabled = false;
            await db.SaveChangesAsync();
        }

        Assert.All(harness.Source.Subscriptions, s => Assert.False(s.Stopped.Task.IsCompleted));
        await harness.Manager.StopProjectAsync(harness.Target.ProjectId);
        Assert.All(harness.Source.Subscriptions, s => Assert.True(s.Stopped.Task.IsCompletedSuccessfully));
        Assert.False(harness.Manager.IsActive(harness.Target.ProjectId, harness.Target.DeploymentId));
        await using var check = harness.Services.CreateAsyncScope();
        Assert.Equal(DeploymentStatus.Running,
            (await check.ServiceProvider.GetRequiredService<AutoMateDbContext>().Deployments.SingleAsync()).Status);
    }

    /// <summary>
    ///     A transient log task failure is retried independently, without restarting a healthy metrics
    ///     source.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Completed_log_source_is_retried_while_metrics_continue()
    {
        await using var harness = await Harness.CreateAsync(true);
        harness.Source.FailFirstLog = true;
        await harness.Manager.RegisterAsync(harness.Target, false);
        await harness.Source.LogRestarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Single(harness.Source.Subscriptions, s => s.Kind == "metrics");
        Assert.Equal(2, harness.Source.Subscriptions.Count(s => s.Kind == "logs"));
        Assert.True(harness.Manager.IsActive(harness.Target.ProjectId, harness.Target.DeploymentId));
    }

    /// <summary>
    ///     Owns an isolated metadata database and the actual host supervisor.
    /// </summary>
    /// <param name="services">Fixture dependency provider.</param>
    /// <param name="keeper">Open SQLite connection retaining the in-memory fixture database.</param>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="source">Synthetic diagnostic or project source selected by the scenario.</param>
    /// <param name="manager">Host-managed local diagnostic supervisor.</param>
    private sealed class Harness(
        ServiceProvider services,
        SqliteConnection keeper,
        DockerDeploymentTarget target,
        TestSource source,
        LocalDeploymentLogStreamManager manager) : IAsyncDisposable
    {
        /// <summary>Scoped service provider.</summary>
        public ServiceProvider Services { get; } = services;

        /// <summary>Registered inventory.</summary>
        public DockerDeploymentTarget Target { get; } = target;

        /// <summary>Cancellable source stub.</summary>
        public TestSource Source { get; } = source;

        /// <summary>Production host supervisor.</summary>
        public LocalDeploymentLogStreamManager Manager { get; } = manager;

        /// <summary>
        ///     Releases owned fixture services, connections and background resources.
        /// </summary>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync(CancellationToken.None);
            Manager.Dispose();
            await Services.DisposeAsync();
            await keeper.DisposeAsync();
        }

        /// <summary>
        ///     Seeds only deployment metadata; log and metric payloads are never written to the database.
        /// </summary>
        /// <param name="consent">Legacy preference value used to verify automatic collection behavior.</param>
        /// <returns>A task returning the configured harness instance or response for this scenario.</returns>
        public static async Task<Harness> CreateAsync(bool consent)
        {
            var connection = "Data Source=supervisor-" + Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(connection);
            await keeper.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
            var source = new TestSource();
            var services = new ServiceCollection()
                .AddScoped<AutoMateDbContext>(_ => new PolicyContext(options, new EphemeralDataProtectionProvider()))
                .AddSingleton<IDockerDiagnosticSource>(source)
                .AddSingleton<IDeploymentRuntimeViewers>(new DeploymentRuntimeViewers(TimeProvider.System))
                .BuildServiceProvider();
            var target = new DockerDeploymentTarget(Guid.NewGuid(), Guid.NewGuid(), "sample",
                [new DockerContainerTarget("sample-web", "web")], DateTimeOffset.UtcNow);
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Deployments.Add(new Deployment
                {
                    Id = target.DeploymentId,
                    Status = DeploymentStatus.Running,
                    CsProject = new CsProject
                    {
                        Name = "web",
                        Path = "web.csproj",
                        Application = new Domain.Entities.Application
                        {
                            Id = target.ProjectId,
                            Name = "sample",
                            SourceType = SourceType.Local,
                            SourcePathOrUrl = "C:/sample",
                            RuntimeDiagnosticsEnabled = consent,
                            User = new LocalUser { Username = "test", Email = $"{target.ProjectId:N}@example.invalid" }
                        }
                    }
                });
                await db.SaveChangesAsync();
            }

            var manager = new LocalDeploymentLogStreamManager(services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<LocalDeploymentLogStreamManager>.Instance);
            await manager.StartAsync(CancellationToken.None);
            return new Harness(services, keeper, target, source, manager);
        }
    }

    /// <summary>
    ///     SQLite representation that supports chronological policy selection.
    /// </summary>
    /// <param name="options">EF options for the fixture metadata database.</param>
    /// <param name="protection">Ephemeral fixture data-protection provider.</param>
    private sealed class PolicyContext(DbContextOptions<AutoMateDbContext> options, IDataProtectionProvider protection)
        : AutoMateDbContext(options, protection)
    {
        /// <summary>
        ///     Adapts timestamp storage and metadata mappings for the SQLite fixture model.
        /// </summary>
        /// <param name="builder">EF builder receiving the fixture model mappings.</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Deployment>().Property(e => e.CreatedAt)
                .HasConversion(e => e.UtcTicks, e => new DateTimeOffset(e, TimeSpan.Zero));
        }
    }

    /// <summary>
    ///     Independent tracked source tasks with delayed cancellation acknowledgement.
    /// </summary>
    private sealed class TestSource : IDockerDiagnosticSource
    {
        /// <summary>Optional provider cleanup delay independent of caller cancellation.</summary>
        public TaskCompletionSource? CleanupGate { get; set; }

        /// <summary>Signals entry into cancellation cleanup.</summary>
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Simulates logs unavailable before Compose recreates the web container.</summary>
        public bool FailFirstLog { get; set; }

        /// <summary>Confirms the supervisor restarted the completed source.</summary>
        public TaskCompletionSource LogRestarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>All source lifetimes started by the supervisor.</summary>
        public ConcurrentQueue<Subscription> Subscriptions { get; } = new();

        /// <summary>Confirms independent runtime tasks were started.</summary>
        public TaskCompletionSource RuntimeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Starts the controlled daemon source and signals subscription to the caller.
        /// </summary>
        /// <param name="target">Verified project, deployment and provider resource inventory; ignored by this fixture.</param>
        /// <param name="subscribed">Called after the diagnostic source has established its subscription.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public Task MonitorDaemonAsync(DockerDeploymentTarget target, Action subscribed,
            CancellationToken cancellationToken)
        {
            var task = RunAsync("daemon", cancellationToken);
            subscribed();
            return task;
        }

        /// <summary>
        ///     Runs the fixture-controlled log source without contacting Docker.
        /// </summary>
        /// <param name="target">Verified project, deployment and provider resource inventory; ignored by this fixture.</param>
        /// <param name="container">Recorded container identity to select; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public Task MonitorContainerAsync(DockerDeploymentTarget target, DockerContainerTarget container,
            CancellationToken cancellationToken)
        {
            return RunAsync("logs", cancellationToken);
        }

        /// <summary>
        ///     Starts the controlled metric source and signals runtime collection.
        /// </summary>
        /// <param name="target">Verified project, deployment and provider resource inventory; ignored by this fixture.</param>
        /// <param name="container">Recorded container identity to select; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public Task MonitorMetricsAsync(DockerDeploymentTarget target, DockerContainerTarget container,
            CancellationToken cancellationToken)
        {
            var task = RunAsync("metrics", cancellationToken);
            RuntimeStarted.TrySetResult();
            return task;
        }

        /// <summary>
        ///     Completes only after source cleanup, making detached replacement races observable.
        /// </summary>
        /// <param name="kind">Synthetic operation or response kind.</param>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>A pending operation that completes through cancellation rather than producing a value.</returns>
        /// <exception cref="IOException">The fixture injects the configured storage or output failure.</exception>
        private async Task RunAsync(string kind, CancellationToken token)
        {
            var subscription = new Subscription(kind);
            Subscriptions.Enqueue(subscription);
            if (kind == "logs" && Subscriptions.Count(s => s.Kind == "logs") > 1) LogRestarted.TrySetResult();
            if (kind == "logs" && FailFirstLog)
            {
                FailFirstLog = false;
                subscription.Stopped.TrySetResult();
                throw new IOException("Unavailable until the container is recreated.");
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                CleanupStarted.TrySetResult();
                if (CleanupGate is not null) await CleanupGate.Task;
                await Task.Delay(25);
                subscription.Stopped.TrySetResult();
            }
        }
    }

    /// <summary>
    ///     Safe source identity and acknowledged cleanup.
    /// </summary>
    /// <param name="Kind">Synthetic operation or response kind.</param>
    private sealed record Subscription(string Kind)
    {
        /// <summary>Completes after cancellation cleanup.</summary>
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}