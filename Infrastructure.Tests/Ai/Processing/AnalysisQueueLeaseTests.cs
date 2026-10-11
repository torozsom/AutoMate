using Application.Abstractions.Ai;
using Application.Ai;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Ai;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using DeploymentAnalysisWorkItem = Application.Abstractions.Ai.DeploymentAnalysisWorkItem;
using WorkEntity = Domain.Entities.DeploymentAnalysisWorkItem;

namespace Infrastructure.Tests.Ai;

/// <summary>Uses independent relational connections to verify atomic acquisition, recovery and lease fencing.</summary>
public sealed class AnalysisQueueLeaseTests
{
    /// <summary>Concurrent workers cannot hold the same current generation.</summary>
    [Fact]
    public async Task Independent_workers_claim_one_job_only_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Queue(db).ClaimNextAsync();
        })));
        var owner = Assert.Single(claims, work => work is not null)!;
        Assert.Equal(fixture.DeploymentId, owner.DeploymentId);
        Assert.NotEqual(Guid.Empty, owner.LeaseId);
        Assert.Equal(1, owner.Attempt);
        await using var read = fixture.NewDb();
        Assert.Equal(owner.LeaseId, (await read.DeploymentAnalysisWorkItems.SingleAsync()).LeaseId);
    }

    /// <summary>Renewal never shortens or revives an expired lease, and obsolete tokens cannot affect recovery.</summary>
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

    /// <summary>Interrupted legacy claims without lease metadata recover without a diagnostic snapshot.</summary>
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

    /// <summary>Terminal results are never reclaimed, even if a legacy completion marker is missing.</summary>
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

    /// <summary>A future retry survives new connections and becomes eligible only when its durable deadline passes.</summary>
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
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Queue(db).ClaimNextAsync();
        })));
        var claim = Assert.Single(claims, item => item is not null)!;
        Assert.Equal(2, claim.ProviderRetryCount);
        Assert.Equal(1, claim.Attempt);
    }

    /// <summary>Concurrent cancellations through independent connections are idempotent and retire queue metadata atomically.</summary>
    [Fact]
    public async Task Independent_owner_cancellations_retire_work_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid owner, analysis;
        await using (var db = fixture.NewDb())
        {
            owner = await db.Deployments.Select(item => item.CsProject!.Application!.UserId).SingleAsync();
            analysis = await db.AiDeploymentAnalyses.Select(item => item.Id).SingleAsync();
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            var service = new DeploymentAnalysisService(db, fixture.Options, AnalysisResultTests.Validator(),
                fixture.Clock,
                new AnalysisEgressPolicyTests.Authorizer(), NullLogger<DeploymentAnalysisService>.Instance);
            return await service.CancelAsync(owner, fixture.DeploymentId, analysis);
        })));
        Assert.All(results, result => Assert.Equal(DeploymentAnalysisCancellationResult.Cancelled, result));
        await using var restarted = fixture.NewDb();
        Assert.Null(await fixture.Queue(restarted).ClaimNextAsync());
        Assert.Equal(AiAnalysisStatus.Cancelled, (await restarted.AiDeploymentAnalyses.SingleAsync()).Status);
        Assert.NotNull((await restarted.DeploymentAnalysisWorkItems.SingleAsync()).CompletedAt);
    }

    /// <summary>Owns a disposable relational metadata file, with no provider or diagnostic storage access.</summary>
    private sealed class Fixture(string path) : IAsyncDisposable
    {
        /// <summary>Shared controlled UTC clock.</summary>
        public TestClock Clock { get; } = new();

        /// <summary>Reloadable synthetic lease settings.</summary>
        public AnalysisEgressPolicyTests.Monitor Options { get; } = new(new AiAnalysisOptions());

        /// <summary>Seeded deployment identity.</summary>
        public Guid DeploymentId { get; private set; }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return ValueTask.CompletedTask;
        }

        /// <summary>Creates an independent connection; pooling is disabled for deterministic file cleanup.</summary>
        public AutoMateDbContext NewDb()
        {
            return new QueueContext(new DbContextOptionsBuilder<AutoMateDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
                .Options);
        }

        /// <summary>Creates the production queue against this fixture clock.</summary>
        public DeploymentAnalysisQueue Queue(AutoMateDbContext db)
        {
            return new DeploymentAnalysisQueue(db, Clock, Options);
        }

        /// <summary>Seeds metadata only, using the real model.</summary>
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
                        Application = new Domain.Entities.Application
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

    /// <summary>Provides deterministic lease expiry without sleeping.</summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>Current controlled UTC time.</summary>
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    /// <summary>SQLite-only representation of PostgreSQL timestamp ordering.</summary>
    private sealed class QueueContext(DbContextOptions<AutoMateDbContext> options)
        : AutoMateDbContext(options, new EphemeralDataProtectionProvider())
    {
        /// <inheritdoc />
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