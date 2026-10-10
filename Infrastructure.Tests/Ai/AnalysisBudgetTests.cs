using System.Data.Common;
using Application.Ai;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Ai;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Work = Application.Abstractions.Ai.DeploymentAnalysisWorkItem;

namespace Infrastructure.Tests.Ai;

/// <summary>Independent relational connections exercise cross-project quotas, rolling rate and reserved spend/capacity.</summary>
public sealed class AnalysisBudgetTests
{
    /// <summary>A failed reservation insert rolls back accounting and detaches only the failed charge for safe retry.</summary>
    [Fact]
    public async Task Reservation_failure_rolls_back_and_reused_context_can_retry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var work = (await fixture.WorkAsync(0))[0];
        var failure = new ChargeFailure();
        await using var db = fixture.Db(failure);
        var guard = new AnalysisBudgetGuard(db, fixture.Settings, fixture.Clock);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => guard.ReserveAttemptAsync(work));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Empty(await db.AiAnalysisBudgetEntries.ToListAsync());
        Assert.Empty(db.ChangeTracker.Entries<AiAnalysisBudgetEntry>());
        failure.Fail = false;
        Assert.Null(await guard.ReserveAttemptAsync(work));
        Assert.Single(await db.AiAnalysisBudgetEntries.ToListAsync());
    }

    /// <summary>Concurrent projects share a tenant quota; deleting its project cannot refund the admitted allowance.</summary>
    [Fact]
    public async Task Concurrent_cross_project_admission_and_deletion_preserve_tenant_allowance()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner, tenantLimit: 1);
        var results = await Task.WhenAll(fixture.Deployments.Take(4).Select(id => Task.Run(async () =>
        {
            await using var db = fixture.Db();
            return await fixture.Service(db).RequestManualAsync(fixture.Owner, id, Guid.NewGuid());
        })));
        var accepted = Assert.Single(results, item => item.Accepted);
        Assert.All(results.Where(item => !item.Accepted), item =>
            Assert.Equal("tenant_quota_exceeded", item.Analysis!.FailureCode));
        await using var check = fixture.Db();
        var project = await check.Deployments.Where(item => item.Id == accepted.Analysis!.DeploymentId)
            .Select(item => item.CsProjectId).SingleAsync();
        await check.CsProjects.Where(item => item.Id == project).ExecuteDeleteAsync();
        Assert.Single(await check.AiAnalysisBudgetEntries.ToListAsync());
        var remaining = fixture.Deployments.First(id => id != accepted.Analysis!.DeploymentId);
        var denied = await fixture.Service(check).RequestManualAsync(fixture.Owner, remaining, Guid.NewGuid());
        Assert.Equal("tenant_quota_exceeded", denied.Analysis!.FailureCode);
    }

    /// <summary>
    ///     Replay/coalescing does not consume new usage; a sixty-second boundary and UTC rollover reset only their
    ///     window.
    /// </summary>
    [Fact]
    public async Task Rolling_rate_replay_and_day_boundaries_use_durable_accounting_clock()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Clock.Now = new DateTimeOffset(2026, 10, 5, 23, 59, 40, TimeSpan.Zero);
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner, rateLimit: 1, tenantLimit: 2);
        await using var db = fixture.Db();
        var request = Guid.NewGuid();
        var first = await fixture.Service(db).RequestManualAsync(fixture.Owner, fixture.Deployments[0], request);
        Assert.True(first.Accepted);
        Assert.True((await fixture.Service(db).RequestManualAsync(fixture.Owner, fixture.Deployments[0], request))
            .Accepted);
        Assert.True(
            (await fixture.Service(db).RequestManualAsync(fixture.Owner, fixture.Deployments[0], Guid.NewGuid()))
            .Accepted);
        Assert.Single(await db.AiAnalysisBudgetEntries.ToListAsync());
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(30);
        Assert.Equal("rate_limited", (await fixture.Service(db).RequestManualAsync(fixture.Owner,
            fixture.Deployments[1], Guid.NewGuid())).Analysis!.FailureCode);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(30);
        Assert.True(
            (await fixture.Service(db).RequestManualAsync(fixture.Owner, fixture.Deployments[1], Guid.NewGuid()))
            .Accepted);
        Assert.Equal(2, await db.AiAnalysisBudgetEntries.CountAsync());
    }

    /// <summary>Concurrent reservations cannot overspend; canceling/deleting work does not refund uncertain provider cost.</summary>
    [Fact]
    public async Task Shared_spend_reservations_are_atomic_and_survive_result_and_project_deletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.CurrentValue =
            AnalysisEgressPolicyTests.Approved(fixture.Owner, dailyBudget: 1, attemptCost: 1);
        var work = await fixture.WorkAsync(0, 1, 2, 3);
        var results = await Task.WhenAll(work.Select(item => Task.Run(async () =>
        {
            await using var db = fixture.Db();
            return await new AnalysisBudgetGuard(db, fixture.Settings, fixture.Clock).ReserveAttemptAsync(item);
        })));
        Assert.Single(results, item => item is null);
        Assert.Equal(3, results.Count(item => item == AnalysisSkipReason.BudgetExceeded));
        await using var check = fixture.Db();
        var charge = await check.AiAnalysisBudgetEntries.SingleAsync();
        Assert.Equal(AnalysisBudgetPolicy.Units(1), charge.ReservedCostUnits);
        var project = await check.AiDeploymentAnalyses.Where(item => item.Id == charge.AnalysisId)
            .Select(item => item.Deployment.CsProjectId).SingleAsync();
        await check.CsProjects.Where(item => item.Id == project).ExecuteDeleteAsync();
        Assert.Single(await check.AiAnalysisBudgetEntries.ToListAsync());
        var remaining = work.First(item => item.AnalysisId != charge.AnalysisId);
        Assert.Equal(AnalysisSkipReason.BudgetExceeded,
            await new AnalysisBudgetGuard(check, fixture.Settings, fixture.Clock).ReserveAttemptAsync(remaining));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        Assert.Null(
            await new AnalysisBudgetGuard(check, fixture.Settings, fixture.Clock).ReserveAttemptAsync(remaining));
    }

    /// <summary>Tenant and global limits count only live charged attempts; another tenant and expired leases are isolated.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shared_concurrency_follows_live_fenced_reservations(bool global)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner,
            tenantConcurrency: 1, globalConcurrency: global ? 1 : 2);
        var work = await fixture.WorkAsync(0, 1, 4);
        await using var db = fixture.Db();
        var guard = new AnalysisBudgetGuard(db, fixture.Settings, fixture.Clock);
        Assert.Null(await guard.ReserveAttemptAsync(work[0]));
        Assert.Equal(AnalysisSkipReason.ConcurrencyExceeded, await guard.ReserveAttemptAsync(work[1]));
        Assert.Equal(global ? AnalysisSkipReason.ConcurrencyExceeded : null, await guard.ReserveAttemptAsync(work[2]));
        await db.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == work[0].AnalysisId)
            .ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.LeaseUntil, fixture.Clock.Now.AddSeconds(-1)));
        Assert.Null(await guard.ReserveAttemptAsync(work[1]));
        Assert.Equal(2, await db.AiAnalysisBudgetEntries.CountAsync(item => item.TenantId == fixture.Owner));
    }

    /// <summary>Default-zero spending and precision/currency changes deny attempts; a recovered lease requires a new charge.</summary>
    [Fact]
    public async Task Retries_currency_changes_and_disabled_budgets_fail_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var work = (await fixture.WorkAsync(0))[0];
        await using var db = fixture.Db();
        var guard = new AnalysisBudgetGuard(db, fixture.Settings, fixture.Clock);
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner, dailyBudget: 0);
        Assert.Equal(AnalysisSkipReason.BudgetNotConfigured, await guard.ReserveAttemptAsync(work));
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner, dailyBudget: 2);
        Assert.Null(await guard.ReserveAttemptAsync(work));
        Assert.Null(await guard.ReserveAttemptAsync(work));
        Assert.Single(await db.AiAnalysisBudgetEntries.ToListAsync());
        var next = work with { LeaseId = Guid.NewGuid() };
        await db.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == work.AnalysisId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseId, next.LeaseId));
        fixture.Settings.CurrentValue =
            AnalysisEgressPolicyTests.Approved(fixture.Owner, dailyBudget: 2, currency: "EUR");
        Assert.Equal(AnalysisSkipReason.BudgetCurrencyMismatch, await guard.ReserveAttemptAsync(next));
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner, dailyBudget: 2);
        Assert.Null(await guard.ReserveAttemptAsync(next));
        Assert.Equal(2, await db.AiAnalysisBudgetEntries.CountAsync());
        Assert.Equal(AnalysisSkipReason.Unavailable, await guard.ReserveAttemptAsync(work));
    }

    /// <summary>Local and cloud deployments use the same owner allowance and preserve existing charges across reloads.</summary>
    [Fact]
    public async Task Sequential_local_and_cloud_attempts_share_allowance_and_reset_at_midnight_utc()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Clock.Now = new DateTimeOffset(2026, 10, 8, 23, 59, 0, TimeSpan.Zero);
        var work = await fixture.WorkAsync(0, 1, 2);
        await using var db = fixture.Db();
        await db.Deployments.Where(item => item.Id == work[1].DeploymentId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CloudGitHubActionRunId, 123L));
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner,
            dailyBudget: 50, attemptCost: 0.10m);
        var guard = new AnalysisBudgetGuard(db, fixture.Settings, fixture.Clock);
        Assert.Null(await guard.ReserveAttemptAsync(work[0]));
        Assert.Null(await guard.ReserveAttemptAsync(work[1]));
        Assert.Equal(2, await db.AiAnalysisBudgetEntries.CountAsync(item => item.IsProviderAttempt));
        Assert.Equal(AnalysisBudgetPolicy.Units(0.20m),
            await db.AiAnalysisBudgetEntries.SumAsync(item => item.ReservedCostUnits));
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner,
            dailyBudget: 0.20m, attemptCost: 0.10m);
        Assert.Equal(AnalysisSkipReason.BudgetExceeded, await guard.ReserveAttemptAsync(work[2]));
        Assert.Equal(2, await db.AiAnalysisBudgetEntries.CountAsync());
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        Assert.Null(await guard.ReserveAttemptAsync(work[2]));
        Assert.Equal(3, await db.AiAnalysisBudgetEntries.CountAsync());
    }

    /// <summary>Missing and malformed amounts have separate authored guidance and never insert a reservation.</summary>
    [Fact]
    public async Task Invalid_and_missing_configuration_have_distinct_codes_without_charging()
    {
        await using var fixture = await Fixture.CreateAsync();
        var work = (await fixture.WorkAsync(0))[0];
        await using var db = fixture.Db();
        var guard = new AnalysisBudgetGuard(db, fixture.Settings, fixture.Clock);
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner,
            dailyBudget: 1, attemptCost: 2);
        Assert.Equal(AnalysisSkipReason.BudgetNotConfigured, await guard.ReserveAttemptAsync(work));
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owner,
            dailyBudget: -1);
        Assert.Equal(AnalysisSkipReason.BudgetConfigurationInvalid, await guard.ReserveAttemptAsync(work));
        Assert.Empty(await db.AiAnalysisBudgetEntries.ToListAsync());
        Assert.Contains("midnight UTC", AnalysisSkipPolicy.Message("budget_exceeded"));
        Assert.Contains("Configure positive", AnalysisSkipPolicy.Message("budget_not_configured"));
        Assert.Contains("invalid", AnalysisSkipPolicy.Message("budget_configuration_invalid"));
        Assert.Contains("Restore", AnalysisSkipPolicy.Message("budget_currency_mismatch"));
    }

    /// <summary>A configuration reload during accounting denies transmission without inserting a charge.</summary>
    [Fact]
    public async Task Reload_during_reservation_is_unavailable_instead_of_exhausted_spending()
    {
        await using var fixture = await Fixture.CreateAsync();
        var work = (await fixture.WorkAsync(0))[0];
        var reload = new ReloadDuringSum(() => fixture.Settings.CurrentValue =
            AnalysisEgressPolicyTests.Approved(fixture.Owner));
        await using var db = fixture.Db(reload);
        Assert.Equal(AnalysisSkipReason.Unavailable,
            await new AnalysisBudgetGuard(db, fixture.Settings, fixture.Clock).ReserveAttemptAsync(work));
        Assert.True(reload.Reloaded);
        Assert.Empty(await db.AiAnalysisBudgetEntries.ToListAsync());
    }

    /// <summary>Reloads one valid snapshot during the relational charge total query.</summary>
    private sealed class ReloadDuringSum(Action reload) : DbCommandInterceptor
    {
        /// <summary>Whether the accounting read triggered a reload.</summary>
        public bool Reloaded { get; private set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Reloaded && command.CommandText.Contains("SUM(", StringComparison.OrdinalIgnoreCase))
            {
                Reloaded = true;
                reload();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Isolated file-backed relational store with two tenants and projects that can be deleted independently.</summary>
    private sealed class Fixture(string path) : IAsyncDisposable
    {
        /// <summary>Primary owner-account tenant.</summary>
        public Guid Owner { get; private set; }

        /// <summary>Four first-tenant deployments followed by a second-tenant deployment.</summary>
        public Guid[] Deployments { get; private set; } = [];

        /// <summary>Mutable validated policy shared by independent scopes.</summary>
        public AnalysisEgressPolicyTests.Monitor Settings { get; private set; } = null!;

        /// <summary>Accounting clock independent of audit timestamps.</summary>
        public TestClock Clock { get; } = new();

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }

        /// <summary>Returns a new connection/context for actual cross-instance serialization.</summary>
        public AutoMateDbContext Db(DbCommandInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<AutoMateDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new BudgetContext(options.Options);
        }

        /// <summary>Production admission and egress policy for the primary owner.</summary>
        public DeploymentAnalysisService Service(AutoMateDbContext db)
        {
            return new DeploymentAnalysisService(db, Settings,
                AnalysisResultTests.Validator(), Clock,
                new AnalysisEgressAuthorizer(db, Settings, new DiagnosticRedactor()),
                NullLogger<DeploymentAnalysisService>.Instance);
        }

        /// <summary>Seeds live processing leases without provider traffic.</summary>
        public async Task<Work[]> WorkAsync(params int[] indexes)
        {
            await using var db = Db();
            var results = new List<Work>();
            foreach (var index in indexes)
            {
                var analysis = new AiDeploymentAnalysis
                {
                    DeploymentId = Deployments[index],
                    Provider = "openai",
                    Model = "fixture",
                    Status = AiAnalysisStatus.Running,
                    ExpiresAt = Clock.Now.AddDays(90),
                    IdempotencyKey = Guid.NewGuid().ToString("N")
                };
                var lease = Guid.NewGuid();
                var until = Clock.Now.AddDays(2);
                db.DeploymentAnalysisWorkItems.Add(new DeploymentAnalysisWorkItem
                    { Analysis = analysis, LeaseId = lease, LeaseUntil = until });
                results.Add(new Work(analysis.Id, Deployments[index], lease, until, 1));
            }

            await db.SaveChangesAsync();
            return results.ToArray();
        }

        /// <summary>Seeds consenting metadata only in a uniquely named disposable database.</summary>
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(),
                "automate-budget-" + Guid.NewGuid().ToString("N") + ".sqlite"));
            await using var db = fixture.Db();
            await db.Database.EnsureCreatedAsync();
            var owner = new LocalUser { Username = "one", Email = "one@example.invalid" };
            var other = new LocalUser { Username = "two", Email = "two@example.invalid" };
            var deployments = Enumerable.Range(0, 5).Select(index => new Deployment
            {
                CsProject = new CsProject
                {
                    Name = "fixture",
                    Path = "fixture.csproj",
                    Configuration = new Configuration { DotNetVersion = "net10.0", AiDiagnosticEgressConsented = true },
                    Application = new Domain.Entities.Application
                    {
                        Name = "fixture",
                        SourcePathOrUrl = "fixture",
                        SourceType = SourceType.Local,
                        User = index < 4 ? owner : other
                    }
                }
            }).ToArray();
            db.Deployments.AddRange(deployments);
            await db.SaveChangesAsync();
            fixture.Owner = owner.Id;
            fixture.Deployments = deployments.Select(item => item.Id).ToArray();
            fixture.Settings = new AnalysisEgressPolicyTests.Monitor(AnalysisEgressPolicyTests.Approved(owner.Id));
            return fixture;
        }
    }

    /// <summary>Mutable UTC clock enables exact rolling-window/day boundaries without sleeps.</summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>Current accounting time.</summary>
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    /// <summary>Injects a bounded accounting-write failure without altering queue metadata.</summary>
    private sealed class ChargeFailure : DbCommandInterceptor
    {
        /// <summary>Whether charge writes should fail.</summary>
        public bool Fail { get; set; } = true;

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.Contains("INSERT INTO \"AiAnalysisBudgetEntries\"",
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic charge failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>SQLite UTC ordering equivalent to the production timestamp columns.</summary>
    private sealed class BudgetContext(DbContextOptions<AutoMateDbContext> options)
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