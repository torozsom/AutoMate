using System.Data.Common;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Application.Ai;
using Application.Orchestration;
using Domain.DTO;
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

namespace Infrastructure.Tests.Ai;

/// <summary>Verifies admission/idempotency/quotas through the production service and independent relational connections.</summary>
public sealed class AnalysisAdmissionTests
{
    /// <summary>
    ///     Stable-ID skips coalesce concurrently, consume no quota/work and replay safe guidance without exposing saved
    ///     payloads.
    /// </summary>
    [Theory]
    [InlineData("quota")]
    [InlineData("egress")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task Skipped_admission_is_idempotent_bounded_and_safe(string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId,
            dailyLimit: reason == "quota" ? 0 : 5, egressEnabled: reason != "egress",
            provider: reason == "missing" ? null : reason == "unknown" ? "unknown" : "openai");
        var request = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Service(db).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request);
        })));
        Assert.All(responses, item =>
        {
            Assert.False(item.Accepted);
            Assert.Equal(AiAnalysisStatus.Skipped, item.Analysis!.Status);
        });
        Assert.Single(responses.Select(item => item.Analysis!.Id).Distinct());
        await using var check = fixture.NewDb();
        Assert.Equal(0, await check.DeploymentAnalysisWorkItems.CountAsync());
        Assert.False((await check.AiAnalysisRequests.SingleAsync()).ConsumesQuota);
        await check.AiDeploymentAnalyses.ExecuteUpdateAsync(update => update
            .SetProperty(item => item.Summary, "private-provider-payload")
            .SetProperty(item => item.Model, "private-model-payload")
            .SetProperty(item => item.FailureCode, "private-error-payload"));
        var view = await fixture.Service(check).GetLatestAsync(fixture.OwnerId, fixture.Deployments[0]);
        Assert.DoesNotContain("private", view!.Summary!);
        Assert.Equal("unavailable", view.FailureCode);
        Assert.Empty(view.RecommendedSteps);
        Assert.Null(view.Model);
        Assert.Null(await fixture.Service(check).GetLatestAsync(Guid.NewGuid(), fixture.Deployments[0]));
        var deleted = await fixture.Service(check).DeleteAsync(fixture.OwnerId, fixture.Deployments[0], view.Id);
        Assert.Equal(DeploymentAnalysisDeletionResult.Deleted, deleted);
        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId);
        Assert.Null((await fixture.Service(check).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request))
            .Analysis);
        Assert.Equal(0, await check.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>
    ///     Cancellation during the first diagnostic preserves the newly created deployment's failure, rather than failing
    ///     an older deployment.
    /// </summary>
    [Fact]
    public async Task Early_local_cancellation_persists_actual_deployment_failure()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();
        using var cancellation = new CancellationTokenSource();
        var notifier = new DeploymentStatusNotifier(NullLogger<DeploymentStatusNotifier>.Instance);
        var orchestrator = new LocalDeploymentOrchestrator(db, null!, null!, null!, null!,
            new DeploymentCapabilities(true, false), new CancelingDiagnostics(cancellation),
            NullLogger<LocalDeploymentOrchestrator>.Instance, notifier, new NoLocalDiagnostics());
        var project = await db.CsProjects.SingleAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.DeployLocalProjectAsync(
            new DeploymentConfigDto { ProjectId = project.AppId, CsProjectId = project.Id }, cancellation.Token));
        var failed = await db.Deployments.AsNoTracking().SingleAsync(item => item.Status == DeploymentStatus.Failed);
        Assert.NotEqual(fixture.Deployments[0], failed.Id);
        Assert.Equal(failed.Id, (await db.FailedDeploymentAnalysisEvents.SingleAsync()).DeploymentId);
        Assert.Equal(DeploymentStatus.Starting,
            (await db.Deployments.AsNoTracking().SingleAsync(item => item.Id == fixture.Deployments[0])).Status);
    }

    /// <summary>Automatic/manual admissions share active work and a single project daily allowance.</summary>
    [Fact]
    public async Task Automatic_admission_coalesces_manual_work_and_shares_quota()
    {
        await using var fixture = await Fixture.CreateAsync(2, 1);
        fixture.Options.CurrentValue =
            AnalysisEgressPolicyTests.Approved(fixture.OwnerId, automatic: true, dailyLimit: 1);
        await using var db = fixture.NewDb();
        var manual = await fixture.Service(db).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0]);
        await db.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        await FailedDeploymentAnalysisDispatcher.DispatchAsync(db, fixture.Service(db), fixture.Clock,
            fixture.Deployments[0]);
        await FailedDeploymentAnalysisDispatcher.DispatchAsync(db, fixture.Service(db), fixture.Clock,
            fixture.Deployments[1]);
        Assert.Equal(manual.Analysis!.Id,
            (await db.AiDeploymentAnalyses.SingleAsync(item => item.Status == AiAnalysisStatus.Queued)).Id);
        Assert.Equal("quota_exceeded",
            (await db.AiDeploymentAnalyses.SingleAsync(item => item.Status == AiAnalysisStatus.Skipped)).FailureCode);
        Assert.Equal(1, await db.DeploymentAnalysisWorkItems.CountAsync());
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync(item => item.ConsumesQuota));
        Assert.Equal(3, await db.AiAnalysisRequests.CountAsync());
        Assert.Equal(0, await db.FailedDeploymentAnalysisEvents.CountAsync(item => item.CompletedAt == null));
    }

    /// <summary>Recovered and concurrent dispatchers admit once, and result/receipt deletion cannot reset the marker.</summary>
    [Fact]
    public async Task Automatic_dispatch_survives_restart_and_result_deletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId, automatic: true);
        await using (var db = fixture.NewDb())
        {
            await db.Deployments.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.Status, DeploymentStatus.Failed));
            Assert.Equal(1, await db.FailedDeploymentAnalysisEvents.CountAsync(item => item.CompletedAt == null));
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            await FailedDeploymentAnalysisDispatcher.DispatchAsync(db, fixture.Service(db), fixture.Clock,
                fixture.Deployments[0]);
        })));
        await using var check = fixture.NewDb();
        Assert.Equal(AiAnalysisTrigger.DeploymentFailed, (await check.AiDeploymentAnalyses.SingleAsync()).Trigger);
        Assert.Equal(1, await check.DeploymentAnalysisWorkItems.CountAsync());
        Assert.NotNull((await check.FailedDeploymentAnalysisEvents.SingleAsync()).CompletedAt);
        await check.AiDeploymentAnalyses.ExecuteDeleteAsync();
        await check.AiAnalysisRequests.ExecuteDeleteAsync();
        await check.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Running));
        await check.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        await FailedDeploymentAnalysisDispatcher.DispatchAsync(check, fixture.Service(check), fixture.Clock,
            fixture.Deployments[0]);
        Assert.Equal(0, await check.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(1, await check.FailedDeploymentAnalysisEvents.CountAsync());
    }

    /// <summary>Policy denial retires wakeups; later enabling/consenting never backfills denied historical failures.</summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("consent")]
    [InlineData("quota")]
    [InlineData("recovered")]
    [InlineData("egress")]
    public async Task Automatic_denials_do_not_create_work_or_backfill(string reason)
    {
        await using var fixture = await Fixture.CreateAsync(limit: reason == "quota" ? 0 : 5);
        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId,
            dailyLimit: reason == "quota" ? 0 : 5, automatic: reason != "disabled", egressEnabled: reason != "egress");
        await using var db = fixture.NewDb();
        await db.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        if (reason == "consent")
            await db.AppConfigs.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.AiDiagnosticEgressConsented, false));
        if (reason == "recovered")
            await db.Deployments.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.Status, DeploymentStatus.Running));
        await FailedDeploymentAnalysisDispatcher.DispatchAsync(db, fixture.Service(db), fixture.Clock,
            fixture.Deployments[0]);
        var skipped = reason is "quota" or "egress";
        Assert.Equal(skipped ? 1 : 0, await db.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(skipped ? 1 : 0, await db.AiAnalysisRequests.CountAsync());
        if (skipped) Assert.Equal(AiAnalysisStatus.Skipped, (await db.AiDeploymentAnalyses.SingleAsync()).Status);
        Assert.Equal(0, await db.AiAnalysisRequests.CountAsync(item => item.ConsumesQuota));
        Assert.NotNull((await db.FailedDeploymentAnalysisEvents.SingleAsync()).CompletedAt);
        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId, automatic: true);
        await db.AppConfigs.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.AiDiagnosticEgressConsented, true));
        await db.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        await FailedDeploymentAnalysisDispatcher.DispatchAsync(db, fixture.Service(db), fixture.Clock,
            fixture.Deployments[0]);
        Assert.Equal(0, await db.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>
    ///     Failure and wakeup roll back together; direct bulk writes and repeated failed writes use actual deployment
    ///     IDs.
    /// </summary>
    [Fact]
    public async Task Failure_capture_is_transactional_and_ignores_repeated_failed_writes()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await using var db = fixture.NewDb();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Deployments.Where(item => item.Id == fixture.Deployments[0])
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, DeploymentStatus.Failed));
            Assert.Equal(fixture.Deployments[0], (await db.FailedDeploymentAnalysisEvents.SingleAsync()).DeploymentId);
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await db.FailedDeploymentAnalysisEvents.CountAsync());
        Assert.All(await db.Deployments.ToListAsync(), item => Assert.Equal(DeploymentStatus.Starting, item.Status));
        await db.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        await db.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        Assert.Equal(2, await db.FailedDeploymentAnalysisEvents.CountAsync());
        await db.Deployments.ExecuteDeleteAsync();
        Assert.Equal(0, await db.FailedDeploymentAnalysisEvents.CountAsync());
    }

    /// <summary>Failed admission rolls back result/work/receipt/completion while preserving the committed deployment failure.</summary>
    [Fact]
    public async Task Automatic_insert_failure_leaves_recoverable_wakeup()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId, automatic: true);
        var failure = new WorkInsertFailure();
        await using var db = fixture.NewDb(failure);
        await db.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        await Assert.ThrowsAsync<DbUpdateException>(() => FailedDeploymentAnalysisDispatcher.DispatchAsync(db,
            fixture.Service(db), fixture.Clock, fixture.Deployments[0]));
        Assert.Null((await db.FailedDeploymentAnalysisEvents.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.Equal(DeploymentStatus.Failed, (await db.Deployments.SingleAsync()).Status);
        Assert.Equal(0, await db.AiAnalysisRequests.CountAsync());
        failure.Fail = false;
        await FailedDeploymentAnalysisDispatcher.DispatchAsync(db, fixture.Service(db), fixture.Clock,
            fixture.Deployments[0]);
        Assert.Equal(1, await db.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>Concurrent legacy or stable-ID requests coalesce into one analysis/work item and one quota charge.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_requests_admit_one_analysis(bool stableId)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return stableId
                ? await fixture.Service(db).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request)
                : await fixture.Service(db).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0]);
        })));
        Assert.All(responses, response => Assert.True(response.Accepted));
        Assert.Single(responses.Select(response => response.Analysis!.Id).Distinct());
        await using var check = fixture.NewDb();
        Assert.Equal(1, await check.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(1, await check.DeploymentAnalysisWorkItems.CountAsync());
        Assert.Equal(1, await check.AiAnalysisRequests.CountAsync());
        Assert.True((await check.AiAnalysisRequests.SingleAsync()).ConsumesQuota);
    }

    /// <summary>Different stable keys for active work become aliases and replay the same completed/canceled result.</summary>
    [Theory]
    [InlineData(AiAnalysisStatus.Completed)]
    [InlineData(AiAnalysisStatus.Cancelled)]
    public async Task Alias_receipts_replay_terminal_analysis_without_new_work(AiAnalysisStatus terminal)
    {
        await using var fixture = await Fixture.CreateAsync();
        var requests = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var responses = await Task.WhenAll(requests.Select(request => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Service(db).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request);
        })));
        Assert.All(responses, response => Assert.True(response.Accepted));
        var analysis = Assert.Single(responses.Select(response => response.Analysis!.Id).Distinct());
        await using var check = fixture.NewDb();
        Assert.Equal(8, await check.AiAnalysisRequests.CountAsync());
        Assert.Equal(1, await check.AiAnalysisRequests.CountAsync(item => item.ConsumesQuota));
        await check.AiDeploymentAnalyses.ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, terminal)
            .SetProperty(item => item.Summary, "A dependency could not start.")
            .SetProperty(item => item.CompletedAt, fixture.Clock.Now));
        foreach (var request in requests)
        {
            var replay = await fixture.Service(check)
                .RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request);
            Assert.True(replay.Accepted);
            Assert.Equal(analysis, replay.Analysis!.Id);
            Assert.Equal(terminal, replay.Analysis.Status);
        }

        Assert.Equal(1, await check.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>Different deployments in one project cannot race past a daily allowance; the project itself is unchanged.</summary>
    [Fact]
    public async Task Concurrent_deployments_obey_project_quota()
    {
        await using var fixture = await Fixture.CreateAsync(8, 2);
        DateTimeOffset updated;
        await using (var db = fixture.NewDb())
        {
            updated = (await db.CsProjects.SingleAsync()).UpdatedAt;
        }

        var responses = await Task.WhenAll(fixture.Deployments.Select(deployment => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Service(db).RequestManualAsync(fixture.OwnerId, deployment, Guid.NewGuid());
        })));
        Assert.Equal(2, responses.Count(response => response.Accepted));
        Assert.All(responses.Where(response => !response.Accepted),
            response => Assert.Equal("quota_exceeded", response.Analysis!.FailureCode));
        await using var check = fixture.NewDb();
        Assert.Equal(2, await check.AiAnalysisRequests.CountAsync(item => item.ConsumesQuota));
        Assert.Equal(2, await check.DeploymentAnalysisWorkItems.CountAsync());
        var project = await check.CsProjects.SingleAsync();
        Assert.Equal("web", project.Name);
        Assert.Equal(updated, project.UpdatedAt);
    }

    /// <summary>Deleting a canceled result neither refunds allowance nor lets the original request create work again.</summary>
    [Fact]
    public async Task Deleted_result_preserves_quota_and_request_identity()
    {
        await using var fixture = await Fixture.CreateAsync(2, 1);
        var request = Guid.NewGuid();
        await using var db = fixture.NewDb();
        var service = fixture.Service(db);
        var admitted = await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request);
        Assert.True(admitted.Accepted);
        Assert.True((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0])).Accepted);
        await service.CancelAsync(fixture.OwnerId, fixture.Deployments[0], admitted.Analysis!.Id);
        await service.DeleteAsync(fixture.OwnerId, fixture.Deployments[0], admitted.Analysis.Id);
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync());
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request)).Accepted);
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[1], Guid.NewGuid()))
            .Accepted);
        Assert.Equal(AiAnalysisStatus.Skipped, (await db.AiDeploymentAnalyses.SingleAsync()).Status);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        Assert.True(
            (await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[1], Guid.NewGuid())).Accepted);
        Assert.Equal(2, await db.AiAnalysisRequests.CountAsync(item => item.ConsumesQuota));
    }

    /// <summary>
    ///     Stable request IDs are deployment/owner scoped; inaccessible IDs and empty/canceled requests do not admit
    ///     work.
    /// </summary>
    [Fact]
    public async Task Request_identity_is_scoped_and_invalid_admission_has_no_side_effects()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await using var db = fixture.NewDb();
        var service = fixture.Service(db);
        var request = Guid.NewGuid();
        Assert.False((await service.RequestManualAsync(Guid.NewGuid(), fixture.Deployments[0], request)).Accepted);
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, Guid.NewGuid(), request)).Accepted);
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], Guid.Empty)).Accepted);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request, canceled.Token));
        Assert.Equal(0, await db.AiAnalysisRequests.CountAsync());
        var first = await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request);
        var second = await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[1], request);
        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.NotEqual(first.Analysis!.Id, second.Analysis!.Id);
        fixture.Options.CurrentValue = new AiAnalysisOptions();
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request)).Accepted);
    }

    /// <summary>
    ///     A failed work insert rolls back result/receipt writes and detaches only failed admission entities for safe
    ///     retry.
    /// </summary>
    [Fact]
    public async Task Admission_failure_rolls_back_and_reused_context_can_retry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var failure = new WorkInsertFailure();
        await using var db = fixture.NewDb(failure);
        var service = fixture.Service(db);
        var request = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request));
        Assert.Equal(0, await db.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(0, await db.DeploymentAnalysisWorkItems.CountAsync());
        Assert.Equal(0, await db.AiAnalysisRequests.CountAsync());
        Assert.Empty(db.ChangeTracker.Entries<AiAnalysisRequest>());
        failure.Fail = false;
        Assert.True((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request)).Accepted);
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync());
    }

    /// <summary>
    ///     Expired result cleanup preserves receipts; separate receipt cleanup is bounded and respects the idempotency
    ///     window.
    /// </summary>
    [Fact]
    public async Task Retention_keeps_live_receipts_and_bounds_receipt_cleanup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();
        var request = Guid.NewGuid();
        await fixture.Service(db).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(2);
        await db.AiDeploymentAnalyses.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.ExpiresAt, fixture.Clock.Now.AddSeconds(-1)));
        Assert.Equal(0, await DeploymentAnalysisRetentionService.DeleteBatchAsync(db, fixture.Clock.Now, default));
        Assert.Equal(AiAnalysisStatus.Cancelled, await db.AiDeploymentAnalyses.Select(a => a.Status).SingleAsync());
        Assert.Equal(0,
            await DeploymentAnalysisRetentionService.DeleteRequestBatchAsync(db, fixture.Clock.Now, default));
        Assert.False((await fixture.Service(db).RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request))
            .Accepted);
        for (var i = 0; i < 1001; i++)
            db.AiAnalysisRequests.Add(new AiAnalysisRequest
            {
                ProjectId = fixture.ProjectId,
                DeploymentId = fixture.Deployments[0],
                AnalysisId = Guid.NewGuid(),
                RequestKey = Guid.NewGuid().ToString("N"),
                AdmissionDay = DateOnly.FromDateTime(fixture.Clock.Now.UtcDateTime),
                ExpiresAt = fixture.Clock.Now.AddSeconds(-1)
            });
        await db.SaveChangesAsync();
        Assert.Equal(1000,
            await DeploymentAnalysisRetentionService.DeleteRequestBatchAsync(db, fixture.Clock.Now, default));
        Assert.Equal(1,
            await DeploymentAnalysisRetentionService.DeleteRequestBatchAsync(db, fixture.Clock.Now, default));
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync());
    }

    /// <summary>
    ///     Receipt aliases are bounded independently of quota; existing stable IDs and legacy active reads remain
    ///     available.
    /// </summary>
    [Fact]
    public async Task Receipt_cap_does_not_block_existing_request_replay()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();
        var request = Guid.NewGuid();
        var service = fixture.Service(db);
        var admitted = await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request);
        for (var i = 1; i < DeploymentAnalysisService.MaximumDailyRequestReceipts; i++)
            db.AiAnalysisRequests.Add(new AiAnalysisRequest
            {
                ProjectId = fixture.ProjectId,
                DeploymentId = fixture.Deployments[0],
                AnalysisId = admitted.Analysis!.Id,
                RequestKey = Guid.NewGuid().ToString("N"),
                AdmissionDay = DateOnly.FromDateTime(fixture.Clock.Now.UtcDateTime),
                ExpiresAt = fixture.Clock.Now.AddDays(90)
            });
        await db.SaveChangesAsync();
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], Guid.NewGuid()))
            .Accepted);
        Assert.True((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], request)).Accepted);
        Assert.True((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0])).Accepted);
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync(item => item.ConsumesQuota));
    }

    /// <summary>Zero allowance denies new work and retains all ownership/consent checks without charging a request.</summary>
    [Fact]
    public async Task Zero_allowance_creates_skipped_receipt_but_revoked_consent_does_not()
    {
        await using var fixture = await Fixture.CreateAsync(limit: 0);
        await using var db = fixture.NewDb();
        var service = fixture.Service(db);
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], Guid.NewGuid()))
            .Accepted);
        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId);
        await db.Set<Configuration>()
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.AiDiagnosticEgressConsented, false));
        Assert.False((await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], Guid.NewGuid()))
            .Accepted);
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync());
        Assert.False((await db.AiAnalysisRequests.SingleAsync()).ConsumesQuota);
        Assert.Equal("quota_exceeded", (await db.AiDeploymentAnalyses.SingleAsync()).FailureCode);
        Assert.Equal(0, await db.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>Stopping changes runtime state but retains the successful historical outcome.</summary>
    [Fact]
    public async Task Successful_outcome_survives_stopping_and_snapshot_remains_immutable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();
        var deployment = await db.Deployments.SingleAsync();
        deployment.ConfigurationSnapshotJson = "{\"ProjectName\":\"original\"}";
        deployment.Status = DeploymentStatus.Running;
        await db.SaveChangesAsync();
        Assert.Equal(DeploymentOutcome.Succeeded, deployment.Outcome);
        deployment.Status = DeploymentStatus.Stopped;
        (await db.CsProjects.Include(p => p.Configuration).SingleAsync()).Configuration!.DotNetVersion = "changed";
        await db.SaveChangesAsync();
        Assert.Equal(DeploymentOutcome.Succeeded, deployment.Outcome);
        Assert.Contains("original", deployment.ConfigurationSnapshotJson);
        Assert.DoesNotContain("changed", deployment.ConfigurationSnapshotJson);
    }

    /// <summary>Every manual status is admitted; old saved results remain owner-scoped and paged after expiry.</summary>
    [Theory]
    [InlineData(DeploymentStatus.Starting)]
    [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Stopped)]
    [InlineData(DeploymentStatus.Failed)]
    public async Task Manual_statuses_and_permanent_result_listing(DeploymentStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();
        var deployment = await db.Deployments.SingleAsync();
        deployment.Status = status;
        await db.SaveChangesAsync();
        var request = Guid.NewGuid();
        var accepted = await fixture.Service(db).RequestManualAsync(fixture.OwnerId, deployment.Id, request);
        Assert.True(accepted.Accepted);
        await db.AiDeploymentAnalyses.ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, AiAnalysisStatus.Completed));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(121);
        await db.AiAnalysisRequests.ExecuteDeleteAsync();
        Assert.Single(await fixture.Service(db).ListAsync(fixture.OwnerId, deployment.Id));
        Assert.Empty(await fixture.Service(db).ListAsync(Guid.NewGuid(), deployment.Id));
        var repeat = await fixture.Service(db).RequestManualAsync(fixture.OwnerId, deployment.Id, request);
        Assert.Equal(accepted.Analysis!.Id, repeat.Analysis!.Id);
        var second = await fixture.Service(db).RequestManualAsync(fixture.OwnerId, deployment.Id, Guid.NewGuid());
        Assert.True(second.Accepted);
        Assert.NotEqual(accepted.Analysis.Id, second.Analysis!.Id);
        Assert.Equal(2, await db.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(2, (await fixture.Service(db).ListAsync(fixture.OwnerId, deployment.Id)).Count);
    }

    /// <summary>Cancels on preparation and simulates unavailable diagnostics on the failure path.</summary>
    private sealed class CancelingDiagnostics(CancellationTokenSource cancellation) : IDeploymentDiagnosticPublisher
    {
        /// <inheritdoc />
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>Provides inert collector lifecycle operations for a failure before Docker is invoked.</summary>
    private sealed class NoLocalDiagnostics : ILocalDeploymentDiagnostics
    {
        /// <inheritdoc />
        public Task RegisterAsync(DockerDeploymentTarget target, bool deploymentOperation,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public bool IsActive(Guid projectId, Guid deploymentId)
        {
            return false;
        }

        /// <inheritdoc />
        public Task SetOperationAsync(Guid projectId, Guid deploymentId, bool active,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Injects a synthetic insert failure after admission entities have been staged.</summary>
    private sealed class WorkInsertFailure : DbCommandInterceptor
    {
        /// <summary>Whether work inserts fail.</summary>
        public bool Fail { get; set; } = true;

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.StartsWith("INSERT", StringComparison.Ordinal) &&
                command.CommandText.Contains("DeploymentAnalysisWorkItems", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic work insert failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Owns isolated metadata and synthetic policy; no context or provider is used.</summary>
    private sealed class Fixture(string path) : IAsyncDisposable
    {
        /// <summary>Controlled UTC admission clock.</summary>
        public TestClock Clock { get; } = new();

        /// <summary>Reloadable operator policy.</summary>
        public AnalysisEgressPolicyTests.Monitor Options { get; private set; } = null!;

        /// <summary>Owner account scope.</summary>
        public Guid OwnerId { get; private set; }

        /// <summary>Shared project quota scope.</summary>
        public Guid ProjectId { get; private set; }

        /// <summary>Deployment identities.</summary>
        public Guid[] Deployments { get; private set; } = [];

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return ValueTask.CompletedTask;
        }

        /// <summary>Independent connections verify actual database serialization.</summary>
        public AutoMateDbContext NewDb(DbCommandInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString());
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new AdmissionContext(options.Options);
        }

        /// <summary>Production admission service and real metadata egress gate.</summary>
        public DeploymentAnalysisService Service(AutoMateDbContext db)
        {
            return new DeploymentAnalysisService(db, Options, AnalysisResultTests.Validator(), Clock,
                new AnalysisEgressAuthorizer(db, Options, new DiagnosticRedactor()),
                NullLogger<DeploymentAnalysisService>.Instance);
        }

        /// <summary>Seeds one consenting project and multiple deployments without any prior admission.</summary>
        public static async Task<Fixture> CreateAsync(int deploymentCount = 1, int limit = 5)
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(),
                "automate-ai-admission-" + Guid.NewGuid().ToString("N") + ".sqlite"));
            await using var db = fixture.NewDb();
            await db.Database.EnsureCreatedAsync();
            // SQLite equivalent exercises application semantics. The opt-in PostgreSQL test executes production trigger SQL.
            await db.Database.ExecuteSqlRawAsync("""
                                                 CREATE TRIGGER capture_failure_update AFTER UPDATE OF Status ON Deployments
                                                 WHEN NEW.Status = 3 AND OLD.Status <> NEW.Status
                                                 BEGIN
                                                     INSERT OR IGNORE INTO FailedDeploymentAnalysisEvents (DeploymentId, CreatedAt)
                                                     VALUES (NEW.Id, 0);
                                                 END;
                                                 CREATE TRIGGER capture_failure_insert AFTER INSERT ON Deployments WHEN NEW.Status = 3
                                                 BEGIN
                                                     INSERT OR IGNORE INTO FailedDeploymentAnalysisEvents (DeploymentId, CreatedAt)
                                                     VALUES (NEW.Id, 0);
                                                 END;
                                                 """);
            var owner = new LocalUser { Username = "test", Email = "test@example.invalid" };
            var project = new CsProject
            {
                Name = "web",
                Path = "web.csproj",
                Configuration = new Configuration { DotNetVersion = "net10.0", AiDiagnosticEgressConsented = true },
                Application = new Domain.Entities.Application
                    { Name = "sample", SourcePathOrUrl = "C:/sample", SourceType = SourceType.Local, User = owner }
            };
            var deployments = Enumerable.Range(0, deploymentCount).Select(_ => new Deployment { CsProject = project })
                .ToArray();
            db.Deployments.AddRange(deployments);
            await db.SaveChangesAsync();
            fixture.OwnerId = owner.Id;
            fixture.ProjectId = project.Id;
            fixture.Deployments = deployments.Select(item => item.Id).ToArray();
            fixture.Options =
                new AnalysisEgressPolicyTests.Monitor(
                    AnalysisEgressPolicyTests.Approved(owner.Id, dailyLimit: limit, retentionDays: 1));
            return fixture;
        }
    }

    /// <summary>Allows UTC day rollover without real waits.</summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>Current UTC time.</summary>
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    /// <summary>SQLite chronological representation of production metadata timestamps.</summary>
    private sealed class AdmissionContext(DbContextOptions<AutoMateDbContext> options)
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