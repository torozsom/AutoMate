using Application.Ai;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Ai;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Work = Application.Abstractions.Ai.DeploymentAnalysisWorkItem;

namespace Infrastructure.Tests.Ai;

/// <summary>Executes real ordered migrations and shared admission/reservation locks in disposable PostgreSQL schemas.</summary>
public sealed class AnalysisBudgetPostgresTests
{
    /// <summary>Independent connections share an owner quota, including after result/project deletion and restart.</summary>
    [AiPostgresFact]
    public async Task Concurrent_admission_and_project_deletion_preserve_owner_allowance()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owners[0], tenantLimit: 1);
        var requests = fixture.Deployments.Take(4).Select(_ => Guid.NewGuid()).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            await using var db = fixture.Db();
            return await fixture.Service(db).RequestManualAsync(fixture.Owners[0], fixture.Deployments[index],
                requests[index]);
        }));
        var accepted = Assert.Single(results, result => result.Accepted).Analysis!;
        Assert.All(results.Where(result => !result.Accepted), result =>
            Assert.Equal("tenant_quota_exceeded", result.Analysis!.FailureCode));
        await using var check = fixture.Db();
        var index = Array.IndexOf(fixture.Deployments, accepted.DeploymentId);
        Assert.True((await fixture.Service(check).RequestManualAsync(fixture.Owners[0], accepted.DeploymentId,
            requests[index])).Accepted);
        Assert.Single(await check.AiAnalysisBudgetEntries.ToListAsync());
        var project = await check.Deployments.Where(item => item.Id == accepted.DeploymentId)
            .Select(item => item.CsProjectId).SingleAsync();
        await check.CsProjects.Where(item => item.Id == project).ExecuteDeleteAsync();
        await using var restarted = fixture.Db();
        Assert.Single(await restarted.AiAnalysisBudgetEntries.ToListAsync());
        var remaining = fixture.Deployments.Take(4).First(id => id != accepted.DeploymentId);
        var denied = await fixture.Service(restarted).RequestManualAsync(fixture.Owners[0], remaining, Guid.NewGuid());
        Assert.Equal("tenant_quota_exceeded", denied.Analysis!.FailureCode);
    }

    /// <summary>The real advisory lock prevents concurrent overspend and reuses one live reservation without refund.</summary>
    [AiPostgresFact]
    public async Task Concurrent_spending_and_recovery_preserve_conservative_reservations()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owners[0], dailyBudget: 1,
            attemptCost: 1);
        var work = await fixture.WorkAsync(0, 1, 2, 3);
        var results = await fixture.ReserveConcurrentlyAsync(work);
        Assert.Single(results, result => result is null);
        Assert.Equal(3, results.Count(result => result == AnalysisSkipReason.BudgetExceeded));
        await using var check = fixture.Db();
        var entry = await check.AiAnalysisBudgetEntries.SingleAsync();
        var charged = work.Single(item => item.LeaseId == entry.LeaseId);
        Assert.Null(await new AnalysisBudgetGuard(check, fixture.Settings, TimeProvider.System)
            .ReserveAttemptAsync(charged));
        Assert.Equal(AnalysisBudgetPolicy.Units(1), entry.ReservedCostUnits);
        var recovered = charged with { LeaseId = Guid.NewGuid() };
        await check.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == charged.AnalysisId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseId, recovered.LeaseId));
        Assert.Equal(AnalysisSkipReason.BudgetExceeded,
            await new AnalysisBudgetGuard(check, fixture.Settings, TimeProvider.System).ReserveAttemptAsync(recovered));
        fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owners[0], dailyBudget: 2,
            attemptCost: 1);
        Assert.Null(
            await new AnalysisBudgetGuard(check, fixture.Settings, TimeProvider.System).ReserveAttemptAsync(recovered));
        Assert.Equal(2, await check.AiAnalysisBudgetEntries.CountAsync());
        await check.AiDeploymentAnalyses.Where(item => item.Id == charged.AnalysisId).ExecuteDeleteAsync();
        Assert.Equal(2, await check.AiAnalysisBudgetEntries.CountAsync());
        Assert.Equal(AnalysisSkipReason.BudgetExceeded,
            await new AnalysisBudgetGuard(check, fixture.Settings, TimeProvider.System)
                .ReserveAttemptAsync(work.First(item => item.AnalysisId != charged.AnalysisId)));
    }

    /// <summary>Distinct tenants share the global reservation cap, while tenant capacity remains independently bounded.</summary>
    [AiPostgresFact]
    public async Task Concurrent_provider_slots_enforce_global_and_tenant_capacity()
    {
        foreach (var global in new[] { 1, 2 })
        {
            await using var fixture = await Fixture.CreateAsync();
            fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.Owners[0], tenantConcurrency: 1,
                globalConcurrency: global);
            var work = await fixture.WorkAsync(0, 1, 4, 5);
            var results = await fixture.ReserveConcurrentlyAsync(work);
            Assert.Equal(global, results.Count(result => result is null));
            Assert.Equal(4 - global, results.Count(result => result == AnalysisSkipReason.ConcurrencyExceeded));
            await using var check = fixture.Db();
            var entries = await check.AiAnalysisBudgetEntries.ToListAsync();
            Assert.All(entries.GroupBy(entry => entry.TenantId), group => Assert.Single(group));
            var released = entries[0];
            await check.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == released.AnalysisId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseUntil,
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
            var ownerIndex = released.TenantId == fixture.Owners[0] ? 0 : 4;
            var next = work.First(item => item.AnalysisId != released.AnalysisId &&
                                          fixture.Deployments.Skip(ownerIndex).Take(2).Contains(item.DeploymentId));
            Assert.Null(
                await new AnalysisBudgetGuard(check, fixture.Settings, TimeProvider.System).ReserveAttemptAsync(next));
            Assert.Equal(global + 1, await check.AiAnalysisBudgetEntries.CountAsync());
        }
    }

    /// <summary>Cancellation while actually waiting for the advisory lock writes no charge and leaves later attempts usable.</summary>
    [AiPostgresFact]
    public async Task Canceled_lock_wait_rolls_back_without_charge_and_next_attempt_succeeds()
    {
        await using var fixture = await Fixture.CreateAsync();
        var work = (await fixture.WorkAsync(0))[0];
        await using var blocker = fixture.Db();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(716204950121)");
        await using var waiting = fixture.Db();
        await waiting.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)waiting.Database.GetDbConnection()).ProcessID;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = new AnalysisBudgetGuard(waiting, fixture.Settings, TimeProvider.System)
            .ReserveAttemptAsync(work, cancellation.Token);
        var observedWait = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid = @pid AND locktype = 'advisory' AND NOT granted)",
                (NpgsqlConnection)blocker.Database.GetDbConnection());
            command.Parameters.AddWithValue("pid", pid);
            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                observedWait = true;
                break;
            }

            await Task.Delay(25);
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.True(observedWait, "The reservation must actually wait on the production advisory lock.");
        await transaction.RollbackAsync();
        Assert.Empty(await waiting.AiAnalysisBudgetEntries.ToListAsync());
        Assert.Null(
            await new AnalysisBudgetGuard(waiting, fixture.Settings, TimeProvider.System).ReserveAttemptAsync(work));
        Assert.Single(await waiting.AiAnalysisBudgetEntries.ToListAsync());
    }

    /// <summary>The actual migration preserves consuming receipts for deleted results without inventing provider charges.</summary>
    [AiPostgresFact]
    public async Task Budget_migration_backfills_only_retained_consuming_admissions()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await using var db = fixture.Db();
        var project = await db.Deployments.Where(item => item.Id == fixture.Deployments[0])
            .Select(item => item.CsProjectId).SingleAsync();
        var deletedAnalysis = Guid.NewGuid();
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var consumes in new[] { true, false })
            db.AiAnalysisRequests.Add(new AiAnalysisRequest
            {
                ProjectId = project,
                DeploymentId = fixture.Deployments[0],
                AnalysisId = consumes ? deletedAnalysis : Guid.NewGuid(),
                RequestKey = Guid.NewGuid().ToString("N"),
                AdmissionDay = day,
                ConsumesQuota = consumes,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(90)
            });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        var admission = await db.AiAnalysisBudgetEntries.SingleAsync();
        Assert.Equal(deletedAnalysis, admission.AnalysisId);
        Assert.Equal(fixture.Owners[0], admission.TenantId);
        Assert.Equal(day, admission.AccountingDay);
        Assert.False(admission.IsProviderAttempt);
        Assert.Null(admission.LeaseId);
        Assert.Equal(0, admission.ReservedCostUnits);
        Assert.Equal("USD", admission.Currency);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    /// <summary>Owns only a generated schema in the explicitly isolated test database; never drops that database.</summary>
    private sealed class Fixture(string schema, string connectionString) : IAsyncDisposable
    {
        /// <summary>Seeded owner-account tenant identifiers.</summary>
        public Guid[] Owners { get; private set; } = [];

        /// <summary>Four deployment projects per owner.</summary>
        public Guid[] Deployments { get; private set; } = [];

        /// <summary>Explicit synthetic runtime policy.</summary>
        public AnalysisEgressPolicyTests.Monitor Settings { get; } =
            new(AnalysisEgressPolicyTests.Approved(Guid.NewGuid()));

        /// <summary>Generated identifier shared by creation and strictly scoped cleanup.</summary>
        private string Schema => schema;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Creates an independent connection and production model with schema-scoped migration history.</summary>
        public AutoMateDbContext Db()
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
                { SearchPath = schema, Pooling = false, Timeout = 10, CommandTimeout = 30 };
            return new AutoMateDbContext(new DbContextOptionsBuilder<AutoMateDbContext>()
                .UseNpgsql(builder.ToString(),
                    settings => settings.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                .UseSnakeCaseNamingConvention().Options, new EphemeralDataProtectionProvider());
        }

        /// <summary>Uses the production metadata service and current-consent authorizer without any provider.</summary>
        public DeploymentAnalysisService Service(AutoMateDbContext db)
        {
            return new DeploymentAnalysisService(db, Settings,
                AnalysisResultTests.Validator(), TimeProvider.System,
                new AnalysisEgressAuthorizer(db, Settings, new DiagnosticRedactor()),
                NullLogger<DeploymentAnalysisService>.Instance);
        }

        /// <summary>Starts concurrent reservations on independent production-provider contexts.</summary>
        public Task<AnalysisSkipReason?[]> ReserveConcurrentlyAsync(Work[] work)
        {
            return Task.WhenAll(work.Select(async item =>
            {
                await using var db = Db();
                return await new AnalysisBudgetGuard(db, Settings, TimeProvider.System).ReserveAttemptAsync(item);
            }));
        }

        /// <summary>Seeds live metadata leases only; no context or provider traffic is needed.</summary>
        public async Task<Work[]> WorkAsync(params int[] indexes)
        {
            await using var db = Db();
            var work = indexes.Select(index =>
            {
                var analysis = new AiDeploymentAnalysis
                {
                    DeploymentId = Deployments[index],
                    Status = AiAnalysisStatus.Running,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
                    IdempotencyKey = Guid.NewGuid().ToString("N")
                };
                var lease = Guid.NewGuid();
                var until = DateTimeOffset.UtcNow.AddMinutes(10);
                db.DeploymentAnalysisWorkItems.Add(new DeploymentAnalysisWorkItem
                    { Analysis = analysis, LeaseId = lease, LeaseUntil = until });
                return new Work(analysis.Id, Deployments[index], lease, until, 1);
            }).ToArray();
            await db.SaveChangesAsync();
            return work;
        }

        /// <summary>Applies real migrations inside a generated schema and seeds consenting owner metadata.</summary>
        public static async Task<Fixture> CreateAsync(bool beforeBudget = false)
        {
            var connectionString = Environment.GetEnvironmentVariable("AUTOMATE_AI_TEST_DB")!;
            var fixture = new Fixture("ai_budget_test_" + Guid.NewGuid().ToString("N"), connectionString);
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE SCHEMA {fixture.Schema}", admin);
            await create.ExecuteNonQueryAsync();
            try
            {
                await using var db = fixture.Db();
                await db.GetService<IMigrator>().MigrateAsync(beforeBudget
                    ? "20261005104851_AddFailedDeploymentAnalysisEvents"
                    : null);
                var owners = Enumerable.Range(0, 2).Select(index => new LocalUser
                    { Username = "fixture-" + index, Email = $"{index}@example.invalid" }).ToArray();
                var deployments = Enumerable.Range(0, 8).Select(index => new Deployment
                {
                    CsProject = new CsProject
                    {
                        Name = "fixture",
                        Path = "fixture.csproj",
                        Configuration = new Configuration
                            { DotNetVersion = "net10.0", AiDiagnosticEgressConsented = true },
                        Application = new Domain.Entities.Application
                        {
                            Name = "fixture",
                            SourcePathOrUrl = "fixture",
                            SourceType = SourceType.Local,
                            User = owners[index / 4]
                        }
                    }
                }).ToArray();
                db.Deployments.AddRange(deployments);
                await db.SaveChangesAsync();
                fixture.Owners = owners.Select(owner => owner.Id).ToArray();
                fixture.Deployments = deployments.Select(deployment => deployment.Id).ToArray();
                fixture.Settings.CurrentValue = AnalysisEgressPolicyTests.Approved(owners[0].Id);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }
    }
}