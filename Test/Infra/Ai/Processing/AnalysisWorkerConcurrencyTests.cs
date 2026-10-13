using System.Collections.Concurrent;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Ai;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DeploymentAnalysisWorkItem = Domain.Entities.DeploymentAnalysisWorkItem;

namespace Test.Infra.Ai;

/// <summary>
///     Exercises hosted processing slots with production leases and independent relational scopes;
///     no live provider calls.
/// </summary>
public sealed class AnalysisWorkerConcurrencyTests
{
    /// <summary>
    ///     Slots fill exactly to the limit, do not preclaim, refill independently and isolate failed
    ///     results/scoped contexts.
    /// </summary>
    /// <param name="concurrency">Configured parallel-operation limit.</param>
    /// <param name="failFirst">Whether to fail the first admitted provider operation.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task Worker_bounds_claims_and_refills_free_slots(int concurrency, bool failFirst)
    {
        await using var fixture = await Fixture.CreateAsync(concurrency, concurrency + 2, failFirst);
        using var worker = fixture.Services.GetRequiredService<DeploymentAnalysisWorker>();
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(fixture.Gate.Starts.Count == concurrency));
            await using (var db = fixture.NewDb())
            {
                Assert.Equal(concurrency,
                    await db.DeploymentAnalysisWorkItems.CountAsync(item => item.AttemptCount == 1));
                Assert.Equal(2, await db.DeploymentAnalysisWorkItems.CountAsync(item => item.AttemptCount == 0));
            }

            fixture.Gate.Permits.Release();
            await WaitUntilAsync(() => Task.FromResult(fixture.Gate.Starts.Count == concurrency + 1));
            Assert.Equal(concurrency, fixture.Gate.Active);
            Assert.Equal(concurrency, fixture.Gate.Peak);
            fixture.Gate.Permits.Release(concurrency + 2);
            await WaitUntilAsync(async () =>
            {
                await using var db = fixture.NewDb();
                return await db.DeploymentAnalysisWorkItems.CountAsync(item => item.CompletedAt != null) ==
                       concurrency + 2;
            });
            await using var check = fixture.NewDb();
            Assert.Equal(concurrency + 2,
                await check.DeploymentAnalysisWorkItems.CountAsync(item => item.AttemptCount == 1));
            Assert.Equal(failFirst ? 1 : 0,
                await check.AiDeploymentAnalyses.CountAsync(item => item.Status == AiAnalysisStatus.Failed));
            Assert.Equal(concurrency + 2 - (failFirst ? 1 : 0),
                await check.AiDeploymentAnalyses.CountAsync(item => item.Status == AiAnalysisStatus.Completed));
            Assert.Equal(concurrency + 2, fixture.Gate.Starts.Select(item => item.Scope).Distinct().Count());
            Assert.All(fixture.Gate.Starts, item => Assert.Contains(item.Scope, fixture.Gate.ContextScopes));
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal(0, fixture.Gate.Active);
    }

    /// <summary>
    ///     Stopping cancels all occupied slots, awaits their releases and leaves unclaimed work
    ///     untouched.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Shutdown_releases_all_slots_without_preclaiming_waiting_work()
    {
        await using var fixture = await Fixture.CreateAsync(3, 5);
        using var worker = fixture.Services.GetRequiredService<DeploymentAnalysisWorker>();
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(fixture.Gate.Starts.Count == 3));
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal(0, fixture.Gate.Active);
        await using var check = fixture.NewDb();
        Assert.Equal(0, await check.DeploymentAnalysisWorkItems.CountAsync(item => item.LeaseId != null));
        Assert.Equal(0, await check.DeploymentAnalysisWorkItems.CountAsync(item => item.CompletedAt != null));
        Assert.Equal(2, await check.DeploymentAnalysisWorkItems.CountAsync(item => item.AttemptCount == 0));
        await using var recovery = fixture.Services.CreateAsyncScope();
        Assert.NotNull(await recovery.ServiceProvider.GetRequiredService<IDeploymentAnalysisQueue>().ClaimNextAsync());
    }

    /// <summary>
    ///     Independent instances each honor their own bound while production queue fencing prevents
    ///     duplicate claims.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Two_instances_claim_distinct_work_with_per_instance_limits()
    {
        await using var fixture = await Fixture.CreateAsync(2, 6);
        using var first = fixture.Services.GetRequiredService<DeploymentAnalysisWorker>();
        using var second = fixture.Services.GetRequiredService<DeploymentAnalysisWorker>();
        await first.StartAsync(default);
        await second.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(fixture.Gate.Starts.Count == 4));
            Assert.Equal(4, fixture.Gate.Starts.Select(item => item.Deployment).Distinct().Count());
            Assert.Equal(4, fixture.Gate.Peak);
            await using var db = fixture.NewDb();
            Assert.Equal(4, await db.DeploymentAnalysisWorkItems.CountAsync(item => item.AttemptCount == 1));
            Assert.Equal(2, await db.DeploymentAnalysisWorkItems.CountAsync(item => item.AttemptCount == 0));
        }
        finally
        {
            await Task.WhenAll(first.StopAsync(default), second.StopAsync(default));
        }

        Assert.Equal(0, fixture.Gate.Active);
    }

    /// <summary>
    ///     Waits for observable progress with a bounded failure deadline instead of assuming scheduler
    ///     timing.
    /// </summary>
    /// <param name="condition">Asynchronous condition polled until it holds or the fixture deadline expires.</param>
    /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, deadline.Token);
    }

    /// <summary>
    ///     Cross-scope provider barrier and concurrency measurements.
    /// </summary>
    /// <param name="failFirst">Whether to fail the first admitted provider operation.</param>
    private sealed class Gate(bool failFirst)
    {
        /// <summary>Current number of blocked/executing provider calls.</summary>
        private int _active;

        /// <summary>Highest simultaneous provider call count.</summary>
        private int _peak;

        /// <summary>Permits controlled completion of one or more provider calls.</summary>
        public SemaphoreSlim Permits { get; } = new(0);

        /// <summary>Context scope identities used to verify provider/context scope pairing.</summary>
        public ConcurrentBag<Guid> ContextScopes { get; } = [];

        /// <summary>Provider starts, identified by actual scope and deployment.</summary>
        public ConcurrentQueue<(Guid Scope, Guid Deployment)> Starts { get; } = new();

        /// <summary>Current provider count, read atomically.</summary>
        public int Active => Volatile.Read(ref _active);

        /// <summary>Peak provider count, read atomically.</summary>
        public int Peak => Volatile.Read(ref _peak);

        /// <summary>
        ///     Records admission, waits for a completion permit or shutdown and always releases the active
        ///     count.
        /// </summary>
        /// <param name="scope">Fixture scope identity.</param>
        /// <param name="deployment">Synthetic deployment identity.</param>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the configured llm analysis response instance or response for this scenario.</returns>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public async Task<LlmAnalysisResponse> RespondAsync(Guid scope, Guid deployment, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            int prior;
            do
            {
                prior = Volatile.Read(ref _peak);
            } while (active > prior && Interlocked.CompareExchange(ref _peak, active, prior) != prior);

            Starts.Enqueue((scope, deployment));
            try
            {
                await Permits.WaitAsync(token);
                if (failFirst && Starts.TryPeek(out var first) && first.Scope == scope)
                    throw new InvalidOperationException("Synthetic provider failure.");
                return new LlmAnalysisResponse("openai", "gpt-5-mini", "Inspect startup configuration.", [], []);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    /// <summary>
    ///     Uses the actual processing scope's DbContext identity and an in-memory context only.
    /// </summary>
    /// <param name="db">Fixture metadata context.</param>
    /// <param name="gate">Shared completion gates and scope observations for the concurrency fixture.</param>
    private sealed class Context(AutoMateDbContext db, Gate gate) : IDeploymentAnalysisContextBuilder
    {
        /// <summary>
        ///     Records the metadata scope and supplies fixed safe context for the worker.
        /// </summary>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment analysis context instance or response for this scenario.</returns>
        public Task<DeploymentAnalysisContext> BuildAsync(Guid deploymentId,
            CancellationToken cancellationToken = default)
        {
            gate.ContextScopes.Add(db.ContextId.InstanceId);
            return Task.FromResult(new DeploymentAnalysisContext("Safe startup diagnostics", []));
        }
    }

    /// <summary>
    ///     Scoped synthetic provider; verifies scope independence without HTTP or credentials.
    /// </summary>
    /// <param name="db">Fixture metadata context.</param>
    /// <param name="gate">Shared completion gates and scope observations for the concurrency fixture.</param>
    private sealed class Provider(AutoMateDbContext db, Gate gate) : ILlmAnalysisProvider
    {
        /// <summary>
        ///     Passes the current scope and required deployment identity to the controlled provider gate.
        /// </summary>
        /// <param name="request">Analysis request supplied to the synthetic provider.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the configured llm analysis response instance or response for this scenario.</returns>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
        public Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            return gate.RespondAsync(db.ContextId.InstanceId,
                request.DeploymentId ?? throw new InvalidOperationException("Missing deployment identity."),
                cancellationToken);
        }
    }

    /// <summary>
    ///     Independent file-backed connections with production queue/worker registrations.
    /// </summary>
    /// <param name="path">Request path used only to select a finite audit area.</param>
    private sealed class Fixture(string path) : IAsyncDisposable
    {
        /// <summary>Worker/processing scope dependency container.</summary>
        public ServiceProvider Services { get; private set; } = null!;

        /// <summary>Provider progress barrier.</summary>
        public Gate Gate { get; private set; } = null!;

        /// <summary>
        ///     Releases owned fixture resources and removes its temporary database files.
        /// </summary>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Gate.Permits.Dispose();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }

        /// <summary>
        ///     Independent context for verification and worker scopes.
        /// </summary>
        /// <returns>An independent metadata context owned by the caller.</returns>
        public AutoMateDbContext NewDb()
        {
            return new WorkerContext(new DbContextOptionsBuilder<AutoMateDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
                .Options);
        }

        /// <summary>
        ///     Seeds distinct deployments with one eligible analysis each; approved policy is synthetic and
        ///     never sent remotely.
        /// </summary>
        /// <param name="concurrency">Configured parallel-operation limit.</param>
        /// <param name="count">Recorded deployment total used to size the bar.</param>
        /// <param name="failFirst">Whether to fail the first admitted provider operation.</param>
        /// <returns>A task returning a seeded fixture owning its database and dependencies.</returns>
        public static async Task<Fixture> CreateAsync(int concurrency, int count, bool failFirst = false)
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(),
                "automate-ai-slots-" + Guid.NewGuid().ToString("N") + ".sqlite"));
            await using var db = fixture.NewDb();
            await db.Database.EnsureCreatedAsync();
            var owner = new LocalUser { Username = "test", Email = "test@example.invalid" };
            var project = new CsProject
            {
                Name = "web",
                Path = "web.csproj",
                Configuration = new Configuration { DotNetVersion = "net10.0", AiDiagnosticEgressConsented = true },
                Application = new Domain.Entities.Application
                    { Name = "sample", SourcePathOrUrl = "C:/sample", SourceType = SourceType.Local, User = owner }
            };
            for (var i = 0; i < count; i++)
                db.DeploymentAnalysisWorkItems.Add(new DeploymentAnalysisWorkItem
                {
                    Analysis = new AiDeploymentAnalysis
                    {
                        Deployment = new Deployment { CsProject = project },
                        Provider = "openai",
                        Model = "gpt-5-mini",
                        Status = AiAnalysisStatus.Queued,
                        Trigger = AiAnalysisTrigger.Manual,
                        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                        IdempotencyKey = Guid.NewGuid().ToString("N")
                    }
                });
            await db.SaveChangesAsync();
            fixture.Gate = new Gate(failFirst);
            var settings = AnalysisEgressPolicyTests.Approved(owner.Id, concurrency: concurrency);
            fixture.Services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
                .AddSingleton(fixture.Gate).AddSingleton(Options.Create(settings))
                .AddSingleton<IOptionsMonitor<AiAnalysisOptions>>(new AnalysisEgressPolicyTests.Monitor(settings))
                .AddScoped<AutoMateDbContext>(_ => fixture.NewDb())
                .AddSingleton<IDiagnosticRedactor, DiagnosticRedactor>()
                .AddSingleton<IAnalysisResultValidator>(AnalysisResultTests.Validator())
                .AddScoped<IAnalysisEgressAuthorizer, AnalysisEgressAuthorizer>()
                .AddScoped<IAnalysisBudgetGuard, AnalysisBudgetGuard>()
                .AddScoped<IDeploymentAnalysisQueue, DeploymentAnalysisQueue>()
                .AddScoped<IDeploymentAnalysisContextBuilder, Context>().AddScoped<ILlmAnalysisProvider, Provider>()
                .AddTransient<DeploymentAnalysisWorker>()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return fixture;
        }
    }

    /// <summary>
    ///     SQLite timestamp representation matching production UTC ordering.
    /// </summary>
    /// <param name="options">EF options for the fixture metadata database.</param>
    private sealed class WorkerContext(DbContextOptions<AutoMateDbContext> options)
        : AutoMateDbContext(options, new EphemeralDataProtectionProvider())
    {
        /// <summary>
        ///     Adapts timestamp storage and metadata mappings for the SQLite fixture model.
        /// </summary>
        /// <param name="builder">EF builder receiving the fixture model mappings.</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var property in builder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
                         .Where(property =>
                             property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks,
                    value => new DateTimeOffset(value, TimeSpan.Zero)));
        }
    }
}
