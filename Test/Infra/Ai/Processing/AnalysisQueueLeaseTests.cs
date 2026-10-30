using Application.Abstractions.Ai.Execution;
using Application.Ai.Policy;
using Domain.Entities.Ai;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Entities.Users;
using Domain.Enums.Ai;
using Domain.Enums.Projects;
using Infrastructure.Ai.Processing;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Test.Infra.Ai.Admission;
using Test.Infra.Ai.ContextAndResults;
using DeploymentAnalysisWorkItem = Application.Abstractions.Ai.Execution.DeploymentAnalysisWorkItem;
using WorkEntity = Domain.Entities.Ai.DeploymentAnalysisWorkItem;

namespace Test.Infra.Ai.Processing;

/// <summary>
///     Uses independent relational connections to verify atomic acquisition, recovery and lease
///     fencing.
/// </summary>
public sealed class AnalysisQueueLeaseTests
{
    /// <summary>
    ///     Concurrent workers cannot hold the same current generation.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Independent_workers_claim_one_job_only_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        var claims = await fixture.ClaimConcurrentlyAsync(8);

        var owner = Assert.Single(claims, work => work is not null)!;
        Assert.Equal(fixture.DeploymentId, owner.DeploymentId);
        Assert.NotEqual(Guid.Empty, owner.LeaseId);
        Assert.Equal(1, owner.Attempt);

        await using var read = fixture.NewDb();

        Assert.Equal(owner.LeaseId, (await read.DeploymentAnalysisWorkItems.SingleAsync()).LeaseId);
    }

    /// <summary>
    ///     Renewal never shortens or revives an expired lease, and obsolete tokens cannot affect
    ///     recovery.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Renewal_and_release_are_fenced_by_current_unexpired_ownership()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();

        var queue = fixture.Queue(db);
        var first = (await queue.ClaimNextAsync())!;

        fixture.Clock.Now += TimeSpan.FromSeconds(10);

        Assert.True(await queue.RenewAsync(first));

        var extended = (await db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).LeaseUntil!.Value;

        Assert.True(extended > first.LeaseUntil);

        fixture.Options.CurrentValue = new AiAnalysisOptions { LeaseDurationSeconds = 30 };

        Assert.True(await queue.RenewAsync(first));
        Assert.Equal(extended, (await db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).LeaseUntil);

        fixture.Clock.Now = extended.AddSeconds(1);

        Assert.False(await queue.RenewAsync(first));
        Assert.False(await queue.ReleaseAsync(first));

        var second = (await queue.ClaimNextAsync())!;

        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.False(await queue.RenewAsync(first));
        Assert.False(await queue.ReleaseAsync(first));
        Assert.True(await queue.ReleaseAsync(second));
        Assert.Equal(3, (await queue.ClaimNextAsync())!.Attempt);
    }

    /// <summary>
    ///     Interrupted legacy claims without lease metadata recover without a diagnostic snapshot.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Legacy_claims_without_lease_metadata_can_recover()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();

        await db.DeploymentAnalysisWorkItems.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.ClaimedAt, fixture.Clock.Now));

        var work = Assert.IsType<DeploymentAnalysisWorkItem>(await fixture.Queue(db).ClaimNextAsync());
        Assert.Equal(1, work.Attempt);
        Assert.Equal(fixture.DeploymentId, work.DeploymentId);
    }

    /// <summary>
    ///     Terminal results are never reclaimed, even if a legacy completion marker is missing.
    /// </summary>
    /// <param name="status">Analysis state under test.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(AiAnalysisStatus.Completed)]
    [InlineData(AiAnalysisStatus.Failed)]
    [InlineData(AiAnalysisStatus.Skipped)]
    [InlineData(AiAnalysisStatus.Cancelled)]
    public async Task Terminal_analyses_do_not_reenter_processing(AiAnalysisStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();

        await db.AiDeploymentAnalyses.ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, status));

        Assert.Null(await fixture.Queue(db).ClaimNextAsync());
    }

    /// <summary>
    ///     A future retry survives new connections and becomes eligible only when its durable deadline
    ///     passes.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Retry_deadline_survives_connection_restart_and_concurrent_claims()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var db = fixture.NewDb())
        {
            await db.DeploymentAnalysisWorkItems.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.NextAttemptAt, fixture.Clock.Now.AddMinutes(1))
                    .SetProperty(item => item.ProviderRetryCount, 2));
        }

        await using (var restarted = fixture.NewDb())
        {
            Assert.Null(await fixture.Queue(restarted).ClaimNextAsync());
        }

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        var claims = await fixture.ClaimConcurrentlyAsync(8);

        var claim = Assert.Single(claims, item => item is not null)!;
        Assert.Equal(2, claim.ProviderRetryCount);
        Assert.Equal(1, claim.Attempt);
    }

    /// <summary>
    ///     Concurrent cancellations through independent connections are idempotent and retire queue
    ///     metadata atomically.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Independent_owner_cancellations_retire_work_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deploymentForCallbacks = fixture.DeploymentId;
        Guid owner, analysis;

        await using (var db = fixture.NewDb())
        {
            owner = await db.Deployments.Select(item => item.CsProject!.Application.UserId).SingleAsync();
            analysis = await db.AiDeploymentAnalyses.Select(item => item.Id).SingleAsync();
        }

        var results = await fixture.CancelConcurrentlyAsync(owner, deploymentForCallbacks, analysis, 8);

        Assert.All(results, result => Assert.Equal(DeploymentAnalysisCancellationResult.Cancelled, result));

        await using var restarted = fixture.NewDb();

        Assert.Null(await fixture.Queue(restarted).ClaimNextAsync());
        Assert.Equal(AiAnalysisStatus.Cancelled, (await restarted.AiDeploymentAnalyses.SingleAsync()).Status);
        Assert.NotNull((await restarted.DeploymentAnalysisWorkItems.SingleAsync()).CompletedAt);
    }

    /// <summary>
    ///     Owns a disposable relational metadata file, with no provider or diagnostic storage access.
    /// </summary>
    /// <param name="path">Request path used only to select a finite audit area.</param>
    private sealed class Fixture(string path) : IAsyncDisposable
    {
        /// <summary>Shared controlled UTC clock.</summary>
        public TestClock Clock { get; } = new();

        /// <summary>Reloadable synthetic lease settings.</summary>
        public AnalysisEgressPolicyTests.Monitor Options { get; } = new(new AiAnalysisOptions());

        /// <summary>Seeded deployment identity.</summary>
        public Guid DeploymentId { get; private set; }

        /// <summary>
        ///     Releases owned fixture resources and removes its temporary database files.
        /// </summary>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");

            return ValueTask.CompletedTask;
        }

        /// <summary>Completes all parallel claims and context disposal within the fixture lifetime.</summary>
        public Task<DeploymentAnalysisWorkItem?[]> ClaimConcurrentlyAsync(int count)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
            {
                await using var db = NewDb();
                return await Queue(db).ClaimNextAsync();
            })));
        }

        /// <summary>Joins competing owner cancellations and their independent metadata contexts.</summary>
        public Task<DeploymentAnalysisCancellationResult[]> CancelConcurrentlyAsync(
            Guid owner, Guid deployment, Guid analysis, int count)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
            {
                await using var db = NewDb();
                var service = new DeploymentAnalysisService(db, Options, AnalysisResultTests.Validator(), Clock,
                    new AnalysisEgressPolicyTests.Authorizer(), NullLogger<DeploymentAnalysisService>.Instance);
                return await service.CancelAsync(owner, deployment, analysis);
            })));
        }

        /// <summary>
        ///     Creates an independent connection; pooling is disabled for deterministic file cleanup.
        /// </summary>
        /// <returns>An independent metadata context owned by the caller.</returns>
        public AutoMateDbContext NewDb()
        {
            return new QueueContext(new DbContextOptionsBuilder<AutoMateDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
                .Options);
        }

        /// <summary>
        ///     Creates the production queue against this fixture clock.
        /// </summary>
        /// <param name="db">Fixture metadata context.</param>
        /// <returns>The configured deployment analysis queue instance or response for this scenario.</returns>
        public DeploymentAnalysisQueue Queue(AutoMateDbContext db)
        {
            return new DeploymentAnalysisQueue(db, Clock, Options);
        }

        /// <summary>
        ///     Seeds metadata only, using the real model.
        /// </summary>
        /// <returns>A task returning a seeded fixture owning its database and dependencies.</returns>
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(),
                "automate-ai-lease-" + Guid.NewGuid().ToString("N") + ".sqlite"));
            await using var db = fixture.NewDb();

            await db.Database.EnsureCreatedAsync();
            var analysis = new AiDeploymentAnalysis
            {
                Status = AiAnalysisStatus.Queued,
                Provider = "openai",
                Model = "gpt-5-mini",
                ExpiresAt = fixture.Clock.Now.AddDays(90),
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Deployment = new Deployment
                {
                    CsProject = new CsProject
                    {
                        Name = "web",
                        Path = "web.csproj",
                        Application = new Domain.Entities.Projects.Application
                        {
                            Name = "sample",
                            SourcePathOrUrl = "C:/sample",
                            SourceType = SourceType.Local,
                            User = new LocalUser { Username = "test", Email = "test@example.invalid" }
                        }
                    }
                }
            };

            db.DeploymentAnalysisWorkItems.Add(new WorkEntity { Analysis = analysis });
            await db.SaveChangesAsync();
            fixture.DeploymentId = analysis.DeploymentId;

            return fixture;
        }
    }

    /// <summary>
    ///     Provides deterministic lease expiry without sleeping.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>Current controlled UTC time.</summary>
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        ///     Returns the controlled UTC clock value.
        /// </summary>
        /// <returns>The fixture-controlled UTC time.</returns>
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    /// <summary>
    ///     SQLite-only representation of PostgreSQL timestamp ordering.
    /// </summary>
    /// <param name="options">EF options for the fixture metadata database.</param>
    private sealed class QueueContext(DbContextOptions<AutoMateDbContext> options)
        : AutoMateDbContext(options, new EphemeralDataProtectionProvider())
    {
        /// <summary>
        ///     Adapts timestamp storage and metadata mappings for the SQLite fixture model.
        /// </summary>
        /// <param name="builder">EF builder receiving the fixture model mappings.</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var property in builder.Model.GetEntityTypes()
                         .SelectMany(entity => entity.GetProperties())
                         .Where(property =>
                             property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks,
                    value => new DateTimeOffset(value, TimeSpan.Zero)));
        }
    }
}
