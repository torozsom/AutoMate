using System.Data.Common;
using System.Text.Json;
using Application.Abstractions.Ai.Execution;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Application.Ai.Context;
using Application.Ai.Policy;
using Application.Orchestration.Status;
using Domain.DTO.Deployment;
using Domain.Entities.Ai;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Entities.Users;
using Domain.Enums.Ai;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Infrastructure.Ai.Admission;
using Infrastructure.Ai.Context;
using Infrastructure.Ai.Processing;
using Infrastructure.ApplicationServices.Orchestration.Local;
using Infrastructure.Data;
using Infrastructure.Diagnostics.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Test.Infra.Ai.ContextAndResults;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Infra.Ai.Admission;

/// <summary>
///     Verifies admission/idempotency/quotas through the production service and independent
///     relational connections.
/// </summary>
public sealed class AnalysisAdmissionTests
{
    /// <summary>An owner missing from the approved cohort receives an actionable skip without work or quota charges.</summary>
    [Fact]
    public async Task Unapproved_current_account_has_an_explicit_safe_skip_reason()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(Guid.NewGuid());
        await using var db = fixture.NewDb();

        var service = fixture.Service(db);
        var result = await service.RequestManualAsync(fixture.OwnerId, fixture.Deployments[0], Guid.NewGuid());

        Assert.False(result.Accepted);
        Assert.Equal("tenant_not_approved", result.Analysis!.FailureCode);
        Assert.Contains("AI-approved account list", result.Analysis.Summary);
        Assert.False(await db.DeploymentAnalysisWorkItems.AnyAsync());
        Assert.False((await db.AiAnalysisRequests.SingleAsync()).ConsumesQuota);

        var saved = await service.GetLatestAsync(fixture.OwnerId, fixture.Deployments[0]);

        Assert.Equal("tenant_not_approved", saved!.FailureCode);
    }

    /// <summary>
    ///     Queued Automatic choices resolve from current persisted status and configuration alone is
    ///     not evidence.
    /// </summary>
    /// <param name="status">Deployment state under test.</param>
    /// <param name="expected">Expected outcome for this scenario.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(DeploymentStatus.Starting, AssessmentKind.Startup)]
    [InlineData(DeploymentStatus.Failed, AssessmentKind.FailureDiagnosis)]
    [InlineData(DeploymentStatus.Running, AssessmentKind.RuntimeOverview)]
    [InlineData(DeploymentStatus.Stopped, AssessmentKind.HistoricalReview)]
    public async Task Queued_selection_uses_collection_status_and_empty_diagnostics_skip(DeploymentStatus status,
        AssessmentKind expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();

        var deploymentId = fixture.Deployments[0];
        var selection = new AssessmentSelection(IncludeMetrics: false);

        var admission = await fixture.Service(db)
            .RequestManualAsync(fixture.OwnerId, deploymentId, Guid.NewGuid(), selection);

        Assert.True(admission.Accepted);

        var now = DateTimeOffset.UtcNow;
        var snapshot = JsonSerializer.Serialize(new DeploymentConfigurationSnapshot("recorded", "Docker Compose",
            null, null, null, "Production", "net10.0", 8080, false, null, null, null, null));

        await db.Deployments
            .Where(d => d.Id == deploymentId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(d => d.Status, status).SetProperty(d => d.CreatedAt, now.AddHours(-2))
                .SetProperty(d => d.UpdatedAt, now).SetProperty(d => d.ConfigurationSnapshotJson, snapshot));

        var builder = new DeploymentAnalysisContextBuilder(db, null!, null!, new DiagnosticRedactor(),
            Options.Create(new AiAnalysisOptions()),
            Options.Create(new TelemetryStorageOptions()), TimeProvider.System);

        var context = await builder.BuildAsync(deploymentId, selection);

        Assert.Equal("", context.Text);
        Assert.Empty(context.EvidenceReferences);
        Assert.Equal(expected, context.Provenance!.EffectiveKind);
        Assert.Equal(status, context.Provenance.ObservedStatus);

        var requested = await db.AiDeploymentAnalyses.AsNoTracking().SingleAsync();

        Assert.Equal(selection.CanonicalJson(), requested.RequestedSelectionJson);
        Assert.False(await db.AiAnalysisBudgetEntries.AnyAsync(entry => entry.IsProviderAttempt));
        Assert.All(await db.AiAnalysisBudgetEntries.ToListAsync(), entry => Assert.Equal(0, entry.ReservedCostUnits));
    }

    /// <summary>
    ///     Preferences remain mutable while admission options and idempotency are immutable and owner
    ///     scoped.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Assessment_preferences_and_request_options_are_independent_and_conflicting_replay_is_denied()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.NewDb();

        var service = fixture.Service(db);
        var deployment = fixture.Deployments[0];
        var request = Guid.NewGuid();
        var chosen = new AssessmentSelection(AssessmentKind.RuntimeOverview, AssessmentSources.Web,
            false, AssessmentRange.LastHour);

        Assert.True(await service.SavePreferencesAsync(fixture.OwnerId, deployment, chosen));
        Assert.Equal(chosen.CanonicalJson(),
            (await service.GetPreferencesAsync(fixture.OwnerId, deployment))!.Selection.CanonicalJson());

        Assert.Null(await service.GetPreferencesAsync(Guid.NewGuid(), deployment));
        Assert.False(await service.SavePreferencesAsync(Guid.NewGuid(), deployment, new AssessmentSelection()));

        var accepted = await service.RequestManualAsync(fixture.OwnerId, deployment, request, chosen);

        Assert.True(accepted.Accepted);

        var changed = chosen with { Sources = AssessmentSources.Database, Kind = AssessmentKind.HistoricalReview };

        Assert.True(await service.SavePreferencesAsync(fixture.OwnerId, deployment, changed));
        Assert.True((await service.RequestManualAsync(fixture.OwnerId, deployment, request, chosen)).Accepted);
        Assert.True((await service.RequestManualAsync(fixture.OwnerId, deployment, request, changed)).Conflict);

        var saved = await db.AiDeploymentAnalyses.AsNoTracking().SingleAsync();

        Assert.Equal(chosen.CanonicalJson(), saved.RequestedSelectionJson);
        Assert.Single(await db.AiAnalysisBudgetEntries.ToArrayAsync());

        await db.AiAnalysisRequests.ExecuteDeleteAsync();

        Assert.True((await service.RequestManualAsync(fixture.OwnerId, deployment, request, changed)).Conflict);
        Assert.True((await service.RequestManualAsync(fixture.OwnerId, deployment, request, chosen)).Accepted);
        Assert.Single(await db.AiDeploymentAnalyses.ToArrayAsync());
    }


    /// <summary>
    ///     Stable-ID skips coalesce concurrently, consume no quota/work and replay safe guidance
    ///     without exposing saved payloads.
    /// </summary>
    /// <param name="reason">Expected finite rejection or failure reason.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("quota")]
    [InlineData("egress")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task Skipped_admission_is_idempotent_bounded_and_safe(string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ownerForCallbacks = fixture.OwnerId;
        var firstDeploymentForCallbacks = fixture.Deployments[0];

        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(ownerForCallbacks,
            dailyLimit: reason == "quota" ? 0 : 5, egressEnabled: reason != "egress",
            provider: reason == "missing" ? null : reason == "unknown" ? "unknown" : "openai");

        var request = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Service(db)
                .RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request);
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

        var view = await fixture.Service(check).GetLatestAsync(ownerForCallbacks, firstDeploymentForCallbacks);

        Assert.DoesNotContain("private", view!.Summary!);
        Assert.Equal("unavailable", view.FailureCode);
        Assert.Empty(view.RecommendedSteps);
        Assert.Null(view.Model);
        Assert.Null(await fixture.Service(check).GetLatestAsync(Guid.NewGuid(), firstDeploymentForCallbacks));

        var deleted = await fixture.Service(check).DeleteAsync(ownerForCallbacks, firstDeploymentForCallbacks, view.Id);

        Assert.Equal(DeploymentAnalysisDeletionResult.Deleted, deleted);

        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(ownerForCallbacks);

        Assert.Null((await fixture.Service(check)
                .RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request))
            .Analysis);

        Assert.Equal(0, await check.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>
    ///     Cancellation during the first diagnostic preserves the newly created deployment's failure,
    ///     rather than failing an older deployment.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Early_local_cancellation_persists_actual_deployment_failure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstDeploymentForCallbacks = fixture.Deployments[0];
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

        Assert.NotEqual(firstDeploymentForCallbacks, failed.Id);
        Assert.Equal(failed.Id, (await db.FailedDeploymentAnalysisEvents.SingleAsync()).DeploymentId);
        Assert.Equal(DeploymentStatus.Starting,
            (await db.Deployments.AsNoTracking().SingleAsync(item => item.Id == firstDeploymentForCallbacks)).Status);
    }

    /// <summary>
    ///     Automatic/manual admissions share active work and a single project daily allowance.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Recovered and concurrent dispatchers admit once, and result/receipt deletion cannot reset
    ///     the marker.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Automatic_dispatch_survives_restart_and_result_deletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstDeploymentForCallbacks = fixture.Deployments[0];

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
                firstDeploymentForCallbacks);
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
            firstDeploymentForCallbacks);

        Assert.Equal(0, await check.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(1, await check.FailedDeploymentAnalysisEvents.CountAsync());
    }

    /// <summary>
    ///     Policy denial retires wakeups; later enabling/consenting never backfills denied historical
    ///     failures.
    /// </summary>
    /// <param name="reason">Expected finite rejection or failure reason.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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
    ///     Failure and wakeup roll back together; direct bulk writes and repeated failed writes use
    ///     actual deployment IDs.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Failure_capture_is_transactional_and_ignores_repeated_failed_writes()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        var firstDeploymentForCallbacks = fixture.Deployments[0];
        await using var db = fixture.NewDb();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Deployments
                .Where(item => item.Id == firstDeploymentForCallbacks)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, DeploymentStatus.Failed));

            Assert.Equal(firstDeploymentForCallbacks,
                (await db.FailedDeploymentAnalysisEvents.SingleAsync()).DeploymentId);

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

    /// <summary>
    ///     Failed admission rolls back result/work/receipt/completion while preserving the committed
    ///     deployment failure.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Automatic_insert_failure_leaves_recoverable_wakeup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstDeploymentForCallbacks = fixture.Deployments[0];

        fixture.Options.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId, automatic: true);
        var failure = new WorkInsertFailure();

        await using var db = fixture.NewDb(failure);

        await db.Deployments.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, DeploymentStatus.Failed));
        await Assert.ThrowsAsync<DbUpdateException>(() => FailedDeploymentAnalysisDispatcher.DispatchAsync(db,
            fixture.Service(db), fixture.Clock, firstDeploymentForCallbacks));

        Assert.Null((await db.FailedDeploymentAnalysisEvents.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.Equal(DeploymentStatus.Failed, (await db.Deployments.SingleAsync()).Status);
        Assert.Equal(0, await db.AiAnalysisRequests.CountAsync());

        failure.Fail = false;

        await FailedDeploymentAnalysisDispatcher.DispatchAsync(db, fixture.Service(db), fixture.Clock,
            firstDeploymentForCallbacks);

        Assert.Equal(1, await db.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>
    ///     Concurrent legacy or stable-ID requests coalesce into one analysis/work item and one quota
    ///     charge.
    /// </summary>
    /// <param name="stableId">Whether the synthetic event has a stable identity.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_requests_admit_one_analysis(bool stableId)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ownerForCallbacks = fixture.OwnerId;
        var firstDeploymentForCallbacks = fixture.Deployments[0];
        var request = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return stableId
                ? await fixture.Service(db).RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request)
                : await fixture.Service(db).RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks);
        })));

        Assert.All(responses, response => Assert.True(response.Accepted));
        Assert.Single(responses.Select(response => response.Analysis!.Id).Distinct());

        await using var check = fixture.NewDb();

        Assert.Equal(1, await check.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(1, await check.DeploymentAnalysisWorkItems.CountAsync());
        Assert.Equal(1, await check.AiAnalysisRequests.CountAsync());
        Assert.True((await check.AiAnalysisRequests.SingleAsync()).ConsumesQuota);
    }

    /// <summary>
    ///     Different stable keys for active work become aliases and replay the same completed/canceled
    ///     result.
    /// </summary>
    /// <param name="terminal">Terminal analysis state under test.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(AiAnalysisStatus.Completed)]
    [InlineData(AiAnalysisStatus.Cancelled)]
    public async Task Alias_receipts_replay_terminal_analysis_without_new_work(AiAnalysisStatus terminal)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ownerForCallbacks = fixture.OwnerId;
        var firstDeploymentForCallbacks = fixture.Deployments[0];
        var requests = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

        var responses = await Task.WhenAll(requests.Select(request => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Service(db)
                .RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request);
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
                .RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request);

            Assert.True(replay.Accepted);
            Assert.Equal(analysis, replay.Analysis!.Id);
            Assert.Equal(terminal, replay.Analysis.Status);
        }

        Assert.Equal(1, await check.DeploymentAnalysisWorkItems.CountAsync());
    }

    /// <summary>
    ///     Different deployments in one project cannot race past a daily allowance; the project itself
    ///     is unchanged.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Concurrent_deployments_obey_project_quota()
    {
        await using var fixture = await Fixture.CreateAsync(8, 2);
        var ownerForCallbacks = fixture.OwnerId;
        DateTimeOffset updated;

        await using (var db = fixture.NewDb())
        {
            updated = (await db.CsProjects.SingleAsync()).UpdatedAt;
        }

        var responses = await Task.WhenAll(fixture.Deployments.Select(deployment => Task.Run(async () =>
        {
            await using var db = fixture.NewDb();
            return await fixture.Service(db).RequestManualAsync(ownerForCallbacks, deployment, Guid.NewGuid());
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

    /// <summary>
    ///     Deleting a canceled result neither refunds allowance nor lets the original request create
    ///     work again.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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
    ///     Stable request IDs are deployment/owner scoped; inaccessible IDs and empty/canceled requests
    ///     do not admit work.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Request_identity_is_scoped_and_invalid_admission_has_no_side_effects()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        var ownerForCallbacks = fixture.OwnerId;
        var firstDeploymentForCallbacks = fixture.Deployments[0];
        await using var db = fixture.NewDb();

        var service = fixture.Service(db);
        var request = Guid.NewGuid();

        Assert.False((await service.RequestManualAsync(Guid.NewGuid(), firstDeploymentForCallbacks, request)).Accepted);
        Assert.False((await service.RequestManualAsync(ownerForCallbacks, Guid.NewGuid(), request)).Accepted);
        Assert.False((await service.RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, Guid.Empty))
            .Accepted);

        using var canceled = new CancellationTokenSource();

        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request, canceled.Token));

        Assert.Equal(0, await db.AiAnalysisRequests.CountAsync(CancellationToken.None));

        var first = await service.RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request);
        var second = await service.RequestManualAsync(ownerForCallbacks, fixture.Deployments[1], request);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.NotEqual(first.Analysis!.Id, second.Analysis!.Id);

        fixture.Options.CurrentValue = new AiAnalysisOptions();

        Assert.False((await service.RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request))
            .Accepted);
    }

    /// <summary>
    ///     A failed work insert rolls back result/receipt writes and detaches only failed admission
    ///     entities for safe retry.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Admission_failure_rolls_back_and_reused_context_can_retry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ownerForCallbacks = fixture.OwnerId;
        var firstDeploymentForCallbacks = fixture.Deployments[0];
        var failure = new WorkInsertFailure();
        await using var db = fixture.NewDb(failure);

        var service = fixture.Service(db);
        var request = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request));

        Assert.Equal(0, await db.AiDeploymentAnalyses.CountAsync());
        Assert.Equal(0, await db.DeploymentAnalysisWorkItems.CountAsync());
        Assert.Equal(0, await db.AiAnalysisRequests.CountAsync());
        Assert.Empty(db.ChangeTracker.Entries<AiAnalysisRequest>());

        failure.Fail = false;

        Assert.True(
            (await service.RequestManualAsync(ownerForCallbacks, firstDeploymentForCallbacks, request)).Accepted);
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync());
    }

    /// <summary>
    ///     Expired result cleanup preserves receipts; separate receipt cleanup is bounded and respects
    ///     the idempotency window.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

        Assert.Equal(0,
            await DeploymentAnalysisRetentionService.DeleteBatchAsync(db, fixture.Clock.Now, CancellationToken.None));
        Assert.Equal(AiAnalysisStatus.Cancelled, await db.AiDeploymentAnalyses.Select(a => a.Status).SingleAsync());
        Assert.Equal(0,
            await DeploymentAnalysisRetentionService.DeleteRequestBatchAsync(db, fixture.Clock.Now,
                CancellationToken.None));
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
            await DeploymentAnalysisRetentionService.DeleteRequestBatchAsync(db, fixture.Clock.Now,
                CancellationToken.None));
        Assert.Equal(1,
            await DeploymentAnalysisRetentionService.DeleteRequestBatchAsync(db, fixture.Clock.Now,
                CancellationToken.None));
        Assert.Equal(1, await db.AiAnalysisRequests.CountAsync());
    }

    /// <summary>
    ///     Receipt aliases are bounded independently of quota; existing stable IDs and legacy active
    ///     reads remain available.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Zero allowance denies new work and retains all ownership/consent checks without charging a
    ///     request.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Stopping changes runtime state but retains the successful historical outcome.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

        (await db.CsProjects
            .Include(p => p.Configuration)
            .SingleAsync()).Configuration!.DotNetVersion = "changed";

        await db.SaveChangesAsync();

        Assert.Equal(DeploymentOutcome.Succeeded, deployment.Outcome);
        Assert.Contains("original", deployment.ConfigurationSnapshotJson);
        Assert.DoesNotContain("changed", deployment.ConfigurationSnapshotJson);
    }

    /// <summary>
    ///     Every manual status is admitted; old saved results remain owner-scoped and paged after
    ///     expiry.
    /// </summary>
    /// <param name="status">Deployment state under test.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Cancels on preparation and simulates unavailable diagnostics on the failure path.
    /// </summary>
    /// <param name="cancellation">Fixture cancellation source controlling the operation.</param>
    private sealed class CancelingDiagnostics(CancellationTokenSource cancellation) : IDeploymentDiagnosticPublisher
    {
        /// <summary>
        ///     Cancels the configured source and injects cancellation at the diagnostic publication
        ///     boundary.
        /// </summary>
        /// <param name="diagnosticEvent">The normalized diagnostic observation to process; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        /// <exception cref="OperationCanceledException">Cancellation is requested or deliberately injected by the fixture.</exception>
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    ///     Provides inert collector lifecycle operations for a failure before Docker is invoked.
    /// </summary>
    private sealed class NoLocalDiagnostics : ILocalDeploymentDiagnostics
    {
        /// <summary>
        ///     Completes immediately without executing live register work.
        /// </summary>
        /// <param name="target">Verified project, deployment and provider resource inventory; ignored by this fixture.</param>
        /// <param name="deploymentOperation">
        ///     Whether a deployment operation currently requires daemon collection; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task RegisterAsync(DockerDeploymentTarget target, bool deploymentOperation,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Reports no active local diagnostics in this admission fixture.
        /// </summary>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <returns>False for every call in this fixture.</returns>
        public bool IsActive(Guid projectId, Guid deploymentId)
        {
            return false;
        }

        /// <summary>
        ///     Completes immediately without executing live set operation work.
        /// </summary>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="active">The current active-operation count; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task SetOperationAsync(Guid projectId, Guid deploymentId, bool active,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Completes immediately without executing live stop project work.
        /// </summary>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task StopProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Injects a synthetic insert failure after admission entities have been staged.
    /// </summary>
    private sealed class WorkInsertFailure : DbCommandInterceptor
    {
        /// <summary>Whether work inserts fail.</summary>
        public bool Fail { get; set; } = true;

        /// <summary>
        ///     Injects a configured failure when EF inserts analysis work; otherwise preserves command
        ///     execution.
        /// </summary>
        /// <param name="command">Database command intercepted by the fixture.</param>
        /// <param name="eventData">EF command interception metadata.</param>
        /// <param name="result">Current EF reader-interception result.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the unchanged EF interception result after fixture checks or failure injection.</returns>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
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

    /// <summary>
    ///     Owns isolated metadata and synthetic policy; no context or provider is used.
    /// </summary>
    /// <param name="path">Request path used only to select a finite audit area.</param>
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

        /// <summary>
        ///     Independent connections verify actual database serialization.
        /// </summary>
        /// <param name="interceptor">Optional EF interceptor used to observe or inject fixture database failures.</param>
        /// <returns>An independent metadata context owned by the caller.</returns>
        public AutoMateDbContext NewDb(DbCommandInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString());

            if (interceptor is not null) options.AddInterceptors(interceptor);

            return new AdmissionContext(options.Options);
        }

        /// <summary>
        ///     Production admission service and real metadata egress gate.
        /// </summary>
        /// <param name="db">Fixture metadata context.</param>
        /// <returns>The configured deployment analysis service instance or response for this scenario.</returns>
        public DeploymentAnalysisService Service(AutoMateDbContext db)
        {
            return new DeploymentAnalysisService(db, Options, AnalysisResultTests.Validator(), Clock,
                new AnalysisEgressAuthorizer(db, Options, new DiagnosticRedactor()),
                NullLogger<DeploymentAnalysisService>.Instance);
        }

        /// <summary>
        ///     Seeds one consenting project and multiple deployments without any prior admission.
        /// </summary>
        /// <param name="deploymentCount">Number of seeded deployments.</param>
        /// <param name="limit">Requested count or configured admission bound.</param>
        /// <returns>A task returning a seeded fixture owning its database and dependencies.</returns>
        public static async Task<Fixture> CreateAsync(int deploymentCount = 1, int limit = 5)
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(),
                "automate-ai-admission-" + Guid.NewGuid().ToString("N") + ".sqlite"));
            await using var db = fixture.NewDb();

            await db.Database.EnsureCreatedAsync();

            // SQLite equivalent exercises application semantics. The opt-in PostgreSQL test executes production trigger SQL.
            await db.Database
                .ExecuteSqlRawAsync("""
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
                Application = new Domain.Entities.Projects.Application
                    { Name = "sample", SourcePathOrUrl = "C:/sample", SourceType = SourceType.Local, User = owner }
            };

            var deployments = Enumerable.Range(0, deploymentCount)
                .Select(_ => new Deployment { CsProject = project })
                .ToArray();

            db.Deployments.AddRange(deployments);
            await db.SaveChangesAsync();

            fixture.OwnerId = owner.Id;
            fixture.ProjectId = project.Id;
            fixture.Deployments = [.. deployments.Select(item => item.Id)];
            fixture.Options =
                new AnalysisEgressPolicyTests.Monitor(
                    AnalysisEgressPolicyTests.Approved(owner.Id, dailyLimit: limit, retentionDays: 1));

            return fixture;
        }
    }

    /// <summary>
    ///     Allows UTC day rollover without real waits.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>Current UTC time.</summary>
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
    ///     SQLite chronological representation of production metadata timestamps.
    /// </summary>
    /// <param name="options">EF options for the fixture metadata database.</param>
    private sealed class AdmissionContext(DbContextOptions<AutoMateDbContext> options)
        : AutoMateDbContext(options, new EphemeralDataProtectionProvider())
    {
        /// <summary>
        ///     Adapts timestamp storage and metadata mappings for the SQLite fixture model.
        /// </summary>
        /// <param name="builder">EF builder receiving the fixture model mappings.</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var property in builder.Model
                         .GetEntityTypes()
                         .SelectMany(entity => entity.GetProperties())
                         .Where(property =>
                             property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks,
                    value => new DateTimeOffset(value, TimeSpan.Zero)));
        }
    }
}
