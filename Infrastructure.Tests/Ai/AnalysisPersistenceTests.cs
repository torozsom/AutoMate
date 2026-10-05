using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DeploymentAnalysisWorkItem = Domain.Entities.DeploymentAnalysisWorkItem;

namespace Infrastructure.Tests.Ai;

/// <summary>Verifies real metadata persistence and authorized safe readback without contacting a provider.</summary>
public sealed class AnalysisPersistenceTests
{
    /// <summary>Actual fenced processing emits correlated stage outcomes without provider payloads or identifier labels.</summary>
    [Theory]
    [InlineData("completed", "completed")]
    [InlineData("failure", "failed")]
    [InlineData("invalid", "failed")]
    [InlineData("skipped", "skipped")]
    [InlineData("retry", "retry_scheduled")]
    [InlineData("canceled", "canceled")]
    public async Task Worker_telemetry_reports_attempt_outcomes_and_balances_active_slots(string mode, string outcome)
    {
        using var token = new CancellationTokenSource();
        await using var fixture = await Fixture.CreateAsync(() => mode switch
        {
            "failure" => Task.FromException<LlmAnalysisResponse>(new IOException("private-error-body")),
            "invalid" => Task.FromResult(AnalysisResultTests.Valid() with
            {
                EvidenceReferences = ["private-fabricated"]
            }),
            "skipped" => Task.FromException<LlmAnalysisResponse>(new AnalysisProviderUnavailableException()),
            "retry" => Task.FromException<LlmAnalysisResponse>(new TransientAnalysisProviderException()),
            "canceled" => Cancel(),
            _ => Task.FromResult(AnalysisResultTests.Valid() with { InputTokens = 12, OutputTokens = 7 })
        });
        var spans = new ConcurrentQueue<(string Name, string? Outcome)>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AutoMate.Analysis",
            Sample = (ref _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (!Equals(activity.GetTagItem("deployment.analysis.id"), fixture.AnalysisId)) return;
                Assert.Empty(activity.Events);
                Assert.DoesNotContain("private", JsonSerializer.Serialize(activity.TagObjects));
                spans.Enqueue((activity.OperationName, activity.GetTagItem("analysis.outcome") as string));
            }
        };
        ActivitySource.AddActivityListener(activities);
        var samples = new ConcurrentQueue<(string Name, long Value)>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "AutoMate.Analysis")
                listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!Equals(Activity.Current?.GetTagItem("deployment.analysis.id"), fixture.AnalysisId)) return;
            foreach (var tag in tags) Assert.Contains(tag.Key, new[] { "analysis.operation", "analysis.outcome" });
            samples.Enqueue((instrument.Name, value));
        });
        metrics.Start();
        if (mode == "canceled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ProcessAsync(token.Token));
        else await fixture.ProcessAsync(token.Token);
        Assert.Contains(("analysis.process", outcome), spans);
        Assert.Contains(("analysis.provider", mode is "skipped" ? "skipped" :
            mode is "canceled" ? "canceled" :
            mode is "completed" ? "completed" : "failed"), spans);
        var active = samples.Where(sample => sample.Name == "automate.analysis.active").ToArray();
        Assert.Equal(new long[] { 1, -1 }, active.Select(sample => sample.Value));
        Assert.Equal(mode == "completed" ? 1 : 0,
            samples.Count(sample => sample.Name == "automate.analysis.input.tokens"));
        if (mode == "completed") Assert.Contains(("automate.analysis.output.tokens", 7L), samples);

        /// <summary>Simulates cooperative cancellation without contacting any provider.</summary>
        Task<LlmAnalysisResponse> Cancel()
        {
            token.Cancel();
            return Task.FromCanceled<LlmAnalysisResponse>(token.Token);
        }
    }

    /// <summary>
    ///     Production builder scopes log/metric reads, excludes foreign events and isolates optional Mimir
    ///     outages/consent.
    /// </summary>
    [Theory]
    [InlineData("available")]
    [InlineData("unavailable")]
    [InlineData("no-consent")]
    public async Task Context_builder_uses_scoped_sources_without_persisting_payloads(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid()));
        var project = await fixture.Db.Deployments.Where(item => item.Id == fixture.DeploymentId)
            .Select(item => item.CsProject!.AppId).SingleAsync();
        var timestamp = DateTimeOffset.UtcNow;
        var logPort = DispatchProxy.Create<IDeploymentDiagnosticStore, ReadPort>();
        ((ReadPort)logPort).Call = (method, args) =>
        {
            Assert.Equal("ReadRecentAsync", method!.Name);
            Assert.Equal(project, args![0]);
            Assert.Equal(fixture.DeploymentId, args[1]);
            Assert.Equal(AnalysisContextSelector.MaximumCandidates, args[2]);
            return Task.FromResult(new DeploymentTerminalHistory([
                new DeploymentTerminalLog(1, project, fixture.DeploymentId, "web", "password=private-secret",
                    Severity: DeploymentDiagnosticSeverity.Error, TimestampUtc: timestamp),
                new DeploymentTerminalLog(2, Guid.NewGuid(), Guid.NewGuid(), "web", "private-foreign-record",
                    TimestampUtc: timestamp)
            ], true));
        };
        var metricCalls = 0;
        var metricPort = DispatchProxy.Create<IDeploymentMetricQuery, ReadPort>();
        ((ReadPort)metricPort).Call = (method, args) =>
        {
            metricCalls++;
            Assert.Equal("ReadAsync", method!.Name);
            Assert.Equal(fixture.OwnerId, args![0]);
            Assert.Equal(project, args[1]);
            Assert.Equal(fixture.DeploymentId, args[2]);
            Assert.Equal(300, args[5]);
            Assert.True((DateTimeOffset)args[4]! - (DateTimeOffset)args[3]! <= TimeSpan.FromHours(1));
            return mode == "unavailable"
                ? Task.FromException<IReadOnlyList<DeploymentMetricPoint>>(
                    new HttpRequestException("private-provider-error"))
                : Task.FromResult<IReadOnlyList<DeploymentMetricPoint>>([
                    new DeploymentMetricPoint("private-container", "automate_cpu_usage_cores", "cores", timestamp, 1, 0,
                        2)
                ]);
        };
        var builder = new DeploymentAnalysisContextBuilder(fixture.Db, logPort, metricPort, new DiagnosticRedactor(),
            Options.Create(new AiAnalysisOptions()),
            Options.Create(new TelemetryStorageOptions { ManagedService = mode == "no-consent" }), TimeProvider.System);
        var context = await builder.BuildAsync(fixture.DeploymentId);
        Assert.DoesNotContain("private", context.Text);
        Assert.Contains("order:1", context.EvidenceReferences);
        Assert.DoesNotContain("order:2", context.EvidenceReferences);
        Assert.Equal(mode == "no-consent" ? 0 : 1, metricCalls);
        using var document = JsonDocument.Parse(context.Text);
        Assert.Equal(mode != "available",
            document.RootElement.GetProperty("coverage").GetProperty("metricsUnavailable").GetBoolean());
        Assert.Empty(await fixture.Db.DeploymentDiagnosticRecords.ToListAsync());
    }

    /// <summary>Structurally valid output with a fabricated reference cannot be saved as a completed result.</summary>
    [Fact]
    public async Task Fabricated_evidence_fails_without_persisting_provider_guidance()
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid() with
        {
            EvidenceReferences = ["order:999"],
            Summary = "Unsupported claim"
        }));
        await fixture.ProcessAsync();
        fixture.Db.ChangeTracker.Clear();
        var analysis = await fixture.Db.AiDeploymentAnalyses.SingleAsync();
        Assert.Equal(AiAnalysisStatus.Failed, analysis.Status);
        Assert.Equal("invalid_response", analysis.FailureCode);
        Assert.Null(analysis.Summary);
        Assert.Null(analysis.EvidenceReferencesJson);
    }

    /// <summary>Worker writes only redacted results plus provenance and optional non-sensitive usage/cost metadata.</summary>
    [Fact]
    public async Task Completed_result_is_redacted_persisted_and_owner_scoped()
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid() with
        {
            Summary = "password=private-summary",
            RecommendedSteps = ["token=private-step"],
            EvidenceReferences = ["order:1"],
            InputTokens = 10,
            OutputTokens = 20,
            EstimatedCost = 0.001m,
            CostCurrency = "USD"
        }));
        await fixture.ProcessAsync(CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var result = await fixture.Db.AiDeploymentAnalyses.SingleAsync();
        Assert.Equal(AiAnalysisStatus.Completed, result.Status);
        Assert.Equal("password=[REDACTED]", result.Summary);
        Assert.DoesNotContain("private", result.RecommendedStepsJson!);
        Assert.DoesNotContain("private", result.EvidenceReferencesJson!);
        Assert.Equal("gpt-5-mini-2025-08-07", result.Model);
        Assert.Equal("gpt-5-mini", result.RequestedModel);
        Assert.Equal(1, result.ResultSchemaVersion);
        Assert.Equal(0.001m, result.EstimatedCost);
        Assert.Equal("USD", result.CostCurrency);
        Assert.NotNull((await fixture.Db.DeploymentAnalysisWorkItems.SingleAsync()).CompletedAt);
        var service = fixture.Services.GetRequiredService<IDeploymentAnalysisService>();
        var view = await service.GetLatestAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.NotNull(view);
        Assert.Equal(result.Model, view.Model);
        Assert.Equal(10, view.InputTokens);
        Assert.Equal(20, view.OutputTokens);
        Assert.Null(await service.GetLatestAsync(Guid.NewGuid(), fixture.DeploymentId));
        Assert.Empty(await fixture.Db.DeploymentDiagnosticRecords.ToListAsync());
    }

    /// <summary>Raw output and exception bodies never become saved summaries for rejected or failed results.</summary>
    [Theory]
    [InlineData("invalid", "invalid_response", AiAnalysisStatus.Failed)]
    [InlineData("exception", "provider_failure", AiAnalysisStatus.Failed)]
    [InlineData("unavailable", "unavailable", AiAnalysisStatus.Skipped)]
    public async Task Failure_paths_store_safe_codes_and_no_partial_provider_result(string kind, string code,
        AiAnalysisStatus status)
    {
        await using var fixture = await Fixture.CreateAsync(() => kind switch
        {
            "invalid" => Task.FromResult(AnalysisResultTests.Valid() with
            {
                Summary = "password=private-value",
                EvidenceReferences = [new string('x', 513)]
            }),
            "exception" => Task.FromException<LlmAnalysisResponse>(
                new InvalidOperationException("password=private-value")),
            "unavailable" => Task.FromException<LlmAnalysisResponse>(new AnalysisProviderUnavailableException()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
        await fixture.ProcessAsync(CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var analysis = await fixture.Db.AiDeploymentAnalyses.SingleAsync();
        Assert.Equal(status, analysis.Status);
        Assert.Equal(code, analysis.FailureCode);
        Assert.DoesNotContain("private-value",
            JsonSerializer.Serialize(analysis,
                new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles }));
        Assert.Null(analysis.RecommendedStepsJson);
        Assert.Null(analysis.EvidenceReferencesJson);
        Assert.Null(analysis.ResultSchemaVersion);
    }

    /// <summary>Caller cancellation does not mark an incomplete response as completed or terminally failed.</summary>
    [Fact]
    public async Task Caller_cancellation_keeps_work_unfinished_without_output()
    {
        using var canceled = new CancellationTokenSource();
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            canceled.Cancel();
            return Task.FromCanceled<LlmAnalysisResponse>(canceled.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.ProcessAsync(canceled.Token));
        fixture.Db.ChangeTracker.Clear();
        Assert.Null((await fixture.Db.DeploymentAnalysisWorkItems.SingleAsync()).CompletedAt);
        Assert.Null((await fixture.Db.AiDeploymentAnalyses.SingleAsync()).Summary);
    }

    /// <summary>Existing unvalidated results are redacted again on read; corrupt JSON produces a safe failed view.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_results_remain_safe_and_do_not_acquire_invented_provenance(bool corrupt)
    {
        await using var fixture =
            await Fixture.CreateAsync(() => throw new InvalidOperationException("Must not invoke provider"));
        var legacy = await fixture.Db.AiDeploymentAnalyses.SingleAsync();
        legacy.Status = AiAnalysisStatus.Completed;
        legacy.Summary = "password=private-value";
        legacy.RecommendedStepsJson = corrupt ? "{password=private-value" : "[\"token=private-value\"]";
        legacy.EvidenceReferencesJson = "[]";
        await fixture.Db.SaveChangesAsync();
        var view = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .GetLatestAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.NotNull(view);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(view));
        Assert.Null(view.ResultSchemaVersion);
        Assert.Null(view.RequestedModel);
        Assert.Equal(corrupt ? AiAnalysisStatus.Failed : AiAnalysisStatus.Completed, view.Status);
    }

    /// <summary>Foreign owners cannot discover/delete results; the owner deletes only inactive or expired work.</summary>
    [Theory]
    [InlineData(AiAnalysisStatus.Queued, false)]
    [InlineData(AiAnalysisStatus.Running, false)]
    [InlineData(AiAnalysisStatus.Completed, true)]
    [InlineData(AiAnalysisStatus.Failed, true)]
    [InlineData(AiAnalysisStatus.Skipped, true)]
    [InlineData(AiAnalysisStatus.Cancelled, true)]
    public async Task Deletion_enforces_owner_and_active_work_policy(AiAnalysisStatus status, bool deletable)
    {
        await using var fixture = await Fixture.CreateAsync(() => throw new InvalidOperationException());
        var analysis = await fixture.Db.AiDeploymentAnalyses.SingleAsync();
        analysis.Status = status;
        await fixture.Db.SaveChangesAsync();
        var service = fixture.Services.GetRequiredService<IDeploymentAnalysisService>();
        Assert.Equal(DeploymentAnalysisDeletionResult.NotFound,
            await service.DeleteAsync(Guid.NewGuid(), fixture.DeploymentId, fixture.AnalysisId));
        Assert.Equal(DeploymentAnalysisDeletionResult.NotFound,
            await service.DeleteAsync(fixture.OwnerId, Guid.NewGuid(), fixture.AnalysisId));
        Assert.Equal(deletable ? DeploymentAnalysisDeletionResult.Deleted : DeploymentAnalysisDeletionResult.InProgress,
            await service.DeleteAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
        Assert.Equal(deletable ? 0 : 1, await fixture.Db.DeploymentAnalysisWorkItems.CountAsync());
        Assert.Equal(1, await fixture.Db.Deployments.CountAsync());
        if (deletable)
            Assert.Equal(DeploymentAnalysisDeletionResult.NotFound,
                await service.DeleteAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
    }

    /// <summary>Expiry prevents read/claim/provider calls immediately, before hourly physical cleanup.</summary>
    [Fact]
    public async Task Expired_work_is_hidden_unclaimed_and_owner_deletable()
    {
        await using var fixture = await Fixture.CreateAsync(() => throw new InvalidOperationException("Must not call"));
        (await fixture.Db.AiDeploymentAnalyses.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await fixture.Db.SaveChangesAsync();
        var service = fixture.Services.GetRequiredService<IDeploymentAnalysisService>();
        Assert.Null(await service.GetLatestAsync(fixture.OwnerId, fixture.DeploymentId));
        Assert.Null(await new DeploymentAnalysisQueue(fixture.Db, TimeProvider.System,
            fixture.Services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>()).ClaimNextAsync());
        await fixture.ProcessAsync(CancellationToken.None);
        Assert.Equal(DeploymentAnalysisDeletionResult.Deleted,
            await service.DeleteAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
        Assert.Empty(await fixture.Db.DeploymentAnalysisWorkItems.ToListAsync());
    }

    /// <summary>Bounded cleanup covers every state, cascades work items and preserves unexpired data/deployments.</summary>
    [Fact]
    public async Task Retention_deletes_bounded_batches_across_all_states()
    {
        await using var fixture = await Fixture.CreateAsync(() => throw new InvalidOperationException());
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1001; i++)
        {
            var expired = new AiDeploymentAnalysis
            {
                DeploymentId = fixture.DeploymentId,
                Provider = "openai",
                Model = "gpt-5-mini",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = (AiAnalysisStatus)(i % 6),
                ExpiresAt = now
            };
            fixture.Db.DeploymentAnalysisWorkItems.Add(new DeploymentAnalysisWorkItem { Analysis = expired });
        }

        await fixture.Db.SaveChangesAsync();
        Assert.Equal(1000,
            await DeploymentAnalysisRetentionService.DeleteBatchAsync(fixture.Db, now, CancellationToken.None));
        Assert.Equal(2, await fixture.Db.DeploymentAnalysisWorkItems.CountAsync());
        Assert.Equal(1,
            await DeploymentAnalysisRetentionService.DeleteBatchAsync(fixture.Db, now, CancellationToken.None));
        Assert.Equal(fixture.AnalysisId, (await fixture.Db.AiDeploymentAnalyses.SingleAsync()).Id);
        Assert.Equal(1, await fixture.Db.Deployments.CountAsync());
        Assert.Empty(await fixture.Db.DeploymentDiagnosticRecords.ToListAsync());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeploymentAnalysisRetentionService.DeleteBatchAsync(fixture.Db, now, canceled.Token));
    }

    /// <summary>An in-flight provider cannot publish results after expiry, with or without physical deletion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_provider_result_is_discarded(bool cleanup)
    {
        Fixture? active = null;
        await using var fixture = await Fixture.CreateAsync(async () =>
        {
            var db = active!.Db;
            await db.AiDeploymentAnalyses.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            if (cleanup)
                await DeploymentAnalysisRetentionService.DeleteBatchAsync(db, DateTimeOffset.UtcNow,
                    CancellationToken.None);
            return AnalysisResultTests.Valid();
        });
        active = fixture;
        await fixture.ProcessAsync(CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        if (cleanup)
        {
            Assert.Empty(await fixture.Db.AiDeploymentAnalyses.ToListAsync());
            Assert.Empty(await fixture.Db.DeploymentAnalysisWorkItems.ToListAsync());
        }
        else
        {
            Assert.Null((await fixture.Db.AiDeploymentAnalyses.SingleAsync()).Summary);
            Assert.Null((await fixture.Db.DeploymentAnalysisWorkItems.SingleAsync()).CompletedAt);
        }
    }

    /// <summary>Actual worker audit outcomes/scopes omit untrusted result and exception text across terminal paths.</summary>
    [Theory]
    [InlineData("completed", "Completed")]
    [InlineData("invalid", "InvalidResult")]
    [InlineData("failed", "Failed")]
    [InlineData("unavailable", "Unavailable")]
    public async Task Worker_audits_only_safe_outcomes_with_correlation(string path, string expected)
    {
        var audit = new AuditLoggerProvider();
        await using var fixture = await Fixture.CreateAsync(() => path switch
        {
            "completed" => Task.FromResult(AnalysisResultTests.Valid() with { Summary = "password=private-result" }),
            "invalid" => Task.FromResult(AnalysisResultTests.Valid() with
            {
                Summary = "password=private-result",
                EvidenceReferences = [new string('x', 513)]
            }),
            "failed" => Task.FromException<LlmAnalysisResponse>(
                new InvalidOperationException("password=private-exception")),
            _ => Task.FromException<LlmAnalysisResponse>(new AnalysisProviderUnavailableException())
        }, audit);
        await fixture.ProcessAsync(CancellationToken.None);
        var terminal = Assert.Single(audit.Entries, entry =>
            entry.GetValueOrDefault("Operation") as string == "Analysis" &&
            entry.GetValueOrDefault("Outcome") as string == expected);
        Assert.Equal(fixture.AnalysisId, terminal["AnalysisId"]);
        Assert.Equal(fixture.DeploymentId, terminal["DeploymentId"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(audit.Entries));
    }

    /// <summary>Empty bounded context has a distinct safe skip reason and never invokes the provider.</summary>
    [Fact]
    public async Task Unsupported_context_skips_without_provider_calls()
    {
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromResult(AnalysisResultTests.Valid());
        });
        var context = (ContextProxy)fixture.Services.GetRequiredService<IDeploymentAnalysisContextBuilder>();
        context.Build = () => Task.FromResult(new DeploymentAnalysisContext(string.Empty, []));
        await fixture.ProcessAsync();
        var view = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .GetLatestAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.Equal(0, calls);
        Assert.Equal(AiAnalysisStatus.Skipped, view!.Status);
        Assert.Equal("unsupported_data", view.FailureCode);
        Assert.Contains("no supported diagnostic data", view.Summary);
        Assert.NotNull((await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).CompletedAt);
    }

    /// <summary>Queued work uses fresh persisted consent and does not contact a provider after revocation.</summary>
    [Fact]
    public async Task Revoked_consent_skips_queued_work_without_provider_calls()
    {
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromResult(AnalysisResultTests.Valid());
        });
        await fixture.Db.Set<Configuration>()
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.AiDiagnosticEgressConsented, false));
        await fixture.ProcessAsync(CancellationToken.None);
        Assert.Equal(0, calls);
        var saved = await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync();
        Assert.Equal(AiAnalysisStatus.Skipped, saved.Status);
        Assert.Equal("unavailable", saved.FailureCode);
    }

    /// <summary>Tenant ownership is resolved from persisted deployment metadata and automatic triggers remain disabled.</summary>
    [Fact]
    public async Task Authorization_rejects_foreign_missing_and_automatic_scopes()
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid()));
        var egress = fixture.Services.GetRequiredService<IAnalysisEgressAuthorizer>();
        Assert.True(await egress.AuthorizeAsync(fixture.DeploymentId, AiAnalysisTrigger.Manual));
        Assert.False(await egress.AuthorizeAsync(Guid.NewGuid(), AiAnalysisTrigger.Manual));
        Assert.False(await egress.AuthorizeAsync(fixture.DeploymentId, AiAnalysisTrigger.DeploymentFailed));
        var foreign = new AnalysisEgressAuthorizer(fixture.Db,
            new AnalysisEgressPolicyTests.Monitor(AnalysisEgressPolicyTests.Approved(Guid.NewGuid())),
            new DiagnosticRedactor());
        Assert.False(await foreign.AuthorizeAsync(fixture.DeploymentId, AiAnalysisTrigger.Manual));
    }

    /// <summary>A consent change while context is fetched is checked again before invoking even a fake provider.</summary>
    [Fact]
    public async Task Consent_revoked_during_context_build_prevents_provider_invocation()
    {
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromResult(AnalysisResultTests.Valid());
        });
        var context = (ContextProxy)fixture.Services.GetRequiredService<IDeploymentAnalysisContextBuilder>();
        context.Build = async () =>
        {
            await fixture.Db.Set<Configuration>().ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.AiDiagnosticEgressConsented, false));
            return new DeploymentAnalysisContext("safe", []);
        };
        await fixture.ProcessAsync(CancellationToken.None);
        Assert.Equal(0, calls);
    }

    /// <summary>Reloading the operator switch denies both admission and already queued work.</summary>
    [Fact]
    public async Task Reloaded_operator_switch_blocks_existing_work_and_new_admission()
    {
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromResult(AnalysisResultTests.Valid());
        });
        var monitor =
            (AnalysisEgressPolicyTests.Monitor)fixture.Services
                .GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>();
        monitor.CurrentValue = new AiAnalysisOptions { Enabled = true };
        var admission = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .RequestManualAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.False(admission.Accepted);
        await fixture.ProcessAsync(CancellationToken.None);
        Assert.Equal(0, calls);
    }

    /// <summary>Model configuration containing a recognized credential produces only safe skipped metadata without new work.</summary>
    [Fact]
    public async Task Secret_bearing_model_configuration_creates_only_safe_skip_metadata()
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid()));
        var monitor =
            (AnalysisEgressPolicyTests.Monitor)fixture.Services
                .GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>();
        monitor.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId, model: "ghp_" + new string('a', 36));
        var result = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .RequestManualAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.False(result.Accepted);
        var skipped = await fixture.Db.AiDeploymentAnalyses.AsNoTracking()
            .SingleAsync(item => item.Status == AiAnalysisStatus.Skipped);
        Assert.Equal("unavailable", skipped.Model);
        Assert.Equal("unavailable", skipped.Provider);
        Assert.Single(await fixture.Db.DeploymentAnalysisWorkItems.ToListAsync());
    }

    /// <summary>New metadata expires under the approved retention policy without extending existing results.</summary>
    [Fact]
    public async Task Admission_applies_configured_result_retention()
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid()));
        await fixture.Db.AiDeploymentAnalyses.ExecuteDeleteAsync();
        fixture.Db.ChangeTracker.Clear();
        var monitor =
            (AnalysisEgressPolicyTests.Monitor)fixture.Services
                .GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>();
        monitor.CurrentValue = AnalysisEgressPolicyTests.Approved(fixture.OwnerId, retentionDays: 7);
        var before = DateTimeOffset.UtcNow;
        var result = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .RequestManualAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.True(result.Accepted);
        var stored = await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync();
        Assert.InRange(stored.ExpiresAt, before.AddDays(7), DateTimeOffset.UtcNow.AddDays(7));
    }

    /// <summary>A late response from an expired generation cannot replace a recovered worker's validated result.</summary>
    [Fact]
    public async Task Stale_worker_response_cannot_overwrite_recovered_completion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<LlmAnalysisResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            if (++calls != 1) return Task.FromResult(AnalysisResultTests.Valid() with { Summary = "Newer diagnosis." });
            entered.TrySetResult();
            return late.Task;
        });
        var queue = fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>();
        var first = (await queue.ClaimNextAsync())!;
        var stale = DeploymentAnalysisWorker.ProcessAsync(fixture.Services, first, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Db.DeploymentAnalysisWorkItems.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var second = (await queue.ClaimNextAsync())!;
        await DeploymentAnalysisWorker.ProcessAsync(fixture.Services, second, default);
        late.SetResult(AnalysisResultTests.Valid() with { Summary = "Older diagnosis." });
        await stale;
        Assert.Equal("Newer diagnosis.", (await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Summary);
        Assert.Equal(second.LeaseId,
            (await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).LeaseId);
        Assert.Equal(2, calls);
    }

    /// <summary>Repeated interrupted acquisitions become a safe terminal outcome without another provider call.</summary>
    [Fact]
    public async Task Exhausted_recovery_attempts_finish_without_context_or_provider_calls()
    {
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromResult(AnalysisResultTests.Valid());
        });
        var queue = fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>();
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.True(await queue.ReleaseAsync((await queue.ClaimNextAsync())!));
        ((ContextProxy)fixture.Services.GetRequiredService<IDeploymentAnalysisContextBuilder>()).Build =
            () => throw new InvalidOperationException("Context must not be loaded after recovery exhaustion.");
        await fixture.ProcessAsync();
        Assert.Equal(0, calls);
        var view = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .GetLatestAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.Equal(AiAnalysisStatus.Failed, view!.Status);
        Assert.Equal("recovery_exhausted", view.FailureCode);
        Assert.NotNull((await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.Null(await queue.ClaimNextAsync());
    }

    /// <summary>Future work is not immediately reclaimed; retries rebuild context and recheck current consent/configuration.</summary>
    [Theory]
    [InlineData("complete")]
    [InlineData("consent")]
    [InlineData("configuration")]
    [InlineData("retry-policy")]
    public async Task Durable_retry_rechecks_policy_and_rebuilds_context(string outcome)
    {
        var calls = 0;
        var builds = 0;
        using var audit = new AuditLoggerProvider();
        await using var fixture = await Fixture.CreateAsync(() => ++calls == 1
            ? Task.FromException<LlmAnalysisResponse>(new TransientAnalysisProviderException(40))
            : Task.FromResult(AnalysisResultTests.Valid()), audit);
        ((ContextProxy)fixture.Services.GetRequiredService<IDeploymentAnalysisContextBuilder>()).Build = () =>
        {
            builds++;
            return Task.FromResult(new DeploymentAnalysisContext("fresh redacted context", ["container/web startup"]));
        };
        var before = DateTimeOffset.UtcNow;
        await fixture.ProcessAsync();
        var row = await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.ProviderRetryCount);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.LeaseId);
        Assert.Null(row.CompletedAt);
        Assert.True(row.NextAttemptAt >= before.AddSeconds(40));
        Assert.Equal(AiAnalysisStatus.Queued,
            (await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Status);
        Assert.Contains(audit.Entries, entry => Equals(entry.GetValueOrDefault("Outcome"), "RetryScheduled"));
        using (var scope = fixture.Services.CreateScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisQueue>().ClaimNextAsync());
        }

        await fixture.Db.DeploymentAnalysisWorkItems.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        if (outcome == "consent")
            await fixture.Db.Set<Configuration>().ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.AiDiagnosticEgressConsented, false));
        if (outcome == "configuration")
            ((AnalysisEgressPolicyTests.Monitor)
                    fixture.Services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>()).CurrentValue =
                new AiAnalysisOptions();
        if (outcome == "retry-policy")
            ((AnalysisEgressPolicyTests.Monitor)
                    fixture.Services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>()).CurrentValue =
                new AiAnalysisOptions { MaximumProviderRetries = 0 };
        await fixture.ProcessAsync();
        var view = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .GetLatestAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.Equal(
            outcome == "complete" ? AiAnalysisStatus.Completed :
            outcome == "retry-policy" ? AiAnalysisStatus.Failed : AiAnalysisStatus.Skipped, view!.Status);
        Assert.Equal(outcome == "complete" ? 2 : 1, calls);
        Assert.Equal(outcome == "complete" ? 2 : 1, builds);
        Assert.NotNull((await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).CompletedAt);
    }

    /// <summary>Only the configured number of transient retries can be scheduled; failure never alters deployment state.</summary>
    [Fact]
    public async Task Repeated_transient_failures_exhaust_durable_retry_budget()
    {
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromException<LlmAnalysisResponse>(new TransientAnalysisProviderException());
        });
        var originalStatus = (await fixture.Db.Deployments.AsNoTracking().SingleAsync()).Status;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await fixture.ProcessAsync();
            await fixture.Db.DeploymentAnalysisWorkItems.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        var view = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .GetLatestAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.Equal(AiAnalysisStatus.Failed, view!.Status);
        Assert.Equal("retry_exhausted", view.FailureCode);
        Assert.Equal(3, calls);
        var work = await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(2, work.ProviderRetryCount);
        Assert.NotNull(work.CompletedAt);
        Assert.Null(await fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>().ClaimNextAsync());
        Assert.Equal(originalStatus, (await fixture.Db.Deployments.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>Long server waits or imminent result expiry terminate without silently scheduling an early attempt.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_wait_or_expiry_does_not_schedule_retry(bool imminentExpiry)
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromException<LlmAnalysisResponse>(
            new TransientAnalysisProviderException(imminentExpiry ? 120 : 3600)));
        if (imminentExpiry)
            await fixture.Db.AiDeploymentAnalyses.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(1)));
        await fixture.ProcessAsync();
        var row = await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(0, row.ProviderRetryCount);
        Assert.Null(row.NextAttemptAt);
        Assert.NotNull(row.CompletedAt);
    }

    /// <summary>A stale transient failure cannot requeue an analysis already completed under a replacement lease.</summary>
    [Fact]
    public async Task Stale_transient_failure_cannot_requeue_newer_result()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<LlmAnalysisResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            if (++calls != 1) return Task.FromResult(AnalysisResultTests.Valid());
            entered.TrySetResult();
            return late.Task;
        });
        var queue = fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>();
        var first = (await queue.ClaimNextAsync())!;
        var stale = DeploymentAnalysisWorker.ProcessAsync(fixture.Services, first, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Db.DeploymentAnalysisWorkItems.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await fixture.ProcessAsync();
        late.SetException(new TransientAnalysisProviderException());
        await stale;
        Assert.Equal(AiAnalysisStatus.Completed,
            (await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, (await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).ProviderRetryCount);
        Assert.Null(await queue.ClaimNextAsync());
    }

    /// <summary>Owner cancellation is durable/idempotent with AI disabled and retires initial or delayed retry work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_cancels_queued_work_with_AI_disabled(bool scheduledRetry)
    {
        var calls = 0;
        using var audit = new AuditLoggerProvider();
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromResult(AnalysisResultTests.Valid());
        }, audit);
        if (scheduledRetry)
            await fixture.Db.DeploymentAnalysisWorkItems.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(5))
                    .SetProperty(item => item.ProviderRetryCount, 1));
        ((AnalysisEgressPolicyTests.Monitor)fixture.Services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>())
            .CurrentValue = new AiAnalysisOptions();
        var service = fixture.Services.GetRequiredService<IDeploymentAnalysisService>();
        var deploymentStatus = (await fixture.Db.Deployments.AsNoTracking().SingleAsync()).Status;
        Assert.Equal(DeploymentAnalysisCancellationResult.Cancelled,
            await service.CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
        Assert.Equal(DeploymentAnalysisCancellationResult.Cancelled,
            await service.CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
        var view = await service.GetLatestAsync(fixture.OwnerId, fixture.DeploymentId);
        Assert.Equal(AiAnalysisStatus.Cancelled, view!.Status);
        Assert.Equal("AI analysis was canceled by its owner.", view.Summary);
        Assert.Empty(view.RecommendedSteps);
        Assert.Empty(view.EvidenceReferences);
        Assert.Null(view.FailureCode);
        Assert.NotNull(view.CompletedAt);
        var work = await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync();
        Assert.NotNull(work.CompletedAt);
        Assert.Null(work.LeaseId);
        Assert.Null(work.LeaseUntil);
        Assert.Null(work.NextAttemptAt);
        Assert.Null(await fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>().ClaimNextAsync());
        await fixture.ProcessAsync();
        Assert.Equal(0, calls);
        Assert.Equal(deploymentStatus, (await fixture.Db.Deployments.AsNoTracking().SingleAsync()).Status);
        Assert.Contains(audit.Entries, entry => Equals(entry.GetValueOrDefault("Outcome"), "CanceledByOwner") &&
                                                Equals(entry.GetValueOrDefault("AnalysisId"), fixture.AnalysisId));
        Assert.Equal(DeploymentAnalysisDeletionResult.Deleted,
            await service.DeleteAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
    }

    /// <summary>Missing/foreign/mismatched/expired identities return the same result without altering active work.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("deployment")]
    [InlineData("analysis")]
    [InlineData("expired")]
    public async Task Cancellation_does_not_disclose_or_modify_inaccessible_analysis(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync(() => throw new InvalidOperationException());
        if (mismatch == "expired")
            await fixture.Db.AiDeploymentAnalyses.ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var result = await fixture.Services.GetRequiredService<IDeploymentAnalysisService>().CancelAsync(
            mismatch == "owner" ? Guid.NewGuid() : fixture.OwnerId,
            mismatch == "deployment" ? Guid.NewGuid() : fixture.DeploymentId,
            mismatch == "analysis" ? Guid.NewGuid() : fixture.AnalysisId);
        Assert.Equal(DeploymentAnalysisCancellationResult.NotFound, result);
        Assert.Equal(AiAnalysisStatus.Queued,
            (await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Status);
        Assert.Null((await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).CompletedAt);
    }

    /// <summary>Terminal analyses preserve their state and guidance; request-token cancellation performs no mutation.</summary>
    [Theory]
    [InlineData(AiAnalysisStatus.Completed)]
    [InlineData(AiAnalysisStatus.Failed)]
    [InlineData(AiAnalysisStatus.Skipped)]
    public async Task Cancellation_preserves_finished_results(AiAnalysisStatus status)
    {
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid()));
        await fixture.ProcessAsync();
        await fixture.Db.AiDeploymentAnalyses.ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.Status, status));
        var original = await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync();
        var service = fixture.Services.GetRequiredService<IDeploymentAnalysisService>();
        Assert.Equal(DeploymentAnalysisCancellationResult.AlreadyFinished,
            await service.CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CancelAsync(fixture.OwnerId,
            fixture.DeploymentId,
            fixture.AnalysisId, canceled.Token));
        var saved = await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync();
        Assert.Equal(original.Status, saved.Status);
        Assert.Equal(original.Summary, saved.Summary);
        Assert.Equal(original.CompletedAt, saved.CompletedAt);
    }

    /// <summary>Uncooperative provider completion or transient failure after cancellation cannot publish or schedule work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_fences_late_provider_result_and_retry(bool transient)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<LlmAnalysisResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            entered.TrySetResult();
            return late.Task;
        });
        var queue = fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>();
        var claim = (await queue.ClaimNextAsync())!;
        var processing = DeploymentAnalysisWorker.ProcessAsync(fixture.Services, claim, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var service = fixture.Services.GetRequiredService<IDeploymentAnalysisService>();
        Assert.Equal(DeploymentAnalysisCancellationResult.Cancelled,
            await service.CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
        if (transient) late.SetException(new TransientAnalysisProviderException());
        else late.SetResult(AnalysisResultTests.Valid());
        await processing;
        Assert.False(await queue.RenewAsync(claim));
        Assert.False(await queue.ReleaseAsync(claim));
        var saved = await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync();
        Assert.Equal(AiAnalysisStatus.Cancelled, saved.Status);
        Assert.Null(saved.Summary);
        Assert.Null(saved.RecommendedStepsJson);
        Assert.Null(saved.FailureCode);
        var work = await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(0, work.ProviderRetryCount);
        Assert.Null(work.NextAttemptAt);
        Assert.NotNull(work.CompletedAt);
        Assert.Null(await queue.ClaimNextAsync());
    }

    /// <summary>Cancellation during context construction prevents the provider from being invoked.</summary>
    [Fact]
    public async Task Cancellation_during_context_build_stops_provider_admission()
    {
        var calls = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
        {
            calls++;
            return Task.FromResult(AnalysisResultTests.Valid());
        });
        ((ContextProxy)fixture.Services.GetRequiredService<IDeploymentAnalysisContextBuilder>()).Build = async () =>
        {
            await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
                .CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId);
            return new DeploymentAnalysisContext("safe diagnostics", []);
        };
        await fixture.ProcessAsync();
        Assert.Equal(0, calls);
        Assert.Equal(AiAnalysisStatus.Cancelled,
            (await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>Renewal detects durable owner cancellation, cancels the provider token and ends without a worker error.</summary>
    [Fact]
    public async Task Lease_coordinator_cancels_local_provider_after_owner_cancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(() => throw new InvalidOperationException(),
            tokenResponse: async token =>
            {
                entered.TrySetResult();
                using var registration = token.Register(() => canceled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return AnalysisResultTests.Valid();
            });
        var claim = (await fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>().ClaimNextAsync())!;
        var worker = new DeploymentAnalysisWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Services.GetRequiredService<ILogger<DeploymentAnalysisWorker>>(), TimeProvider.System,
            fixture.Services.GetRequiredService<IOptions<AiAnalysisOptions>>());
        var processing = worker.RunLeasedAsync(fixture.Services,
            claim with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(3) }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Services.GetRequiredService<IDeploymentAnalysisService>()
            .CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AiAnalysisStatus.Cancelled,
            (await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>A queue retirement failure rolls back cancellation state and does not emit a completed owner audit event.</summary>
    [Fact]
    public async Task Cancellation_rolls_back_when_queue_retirement_fails()
    {
        var interceptor = new RetirementFailure();
        using var audit = new AuditLoggerProvider();
        await using var fixture = await Fixture.CreateAsync(() => Task.FromResult(AnalysisResultTests.Valid()), audit,
            interceptor: interceptor);
        interceptor.Fail = true;
        var service = fixture.Services.GetRequiredService<IDeploymentAnalysisService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
        Assert.Equal(AiAnalysisStatus.Queued,
            (await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Status);
        Assert.Null((await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.DoesNotContain(audit.Entries, entry => Equals(entry.GetValueOrDefault("Outcome"), "CanceledByOwner"));
        interceptor.Fail = false;
        Assert.Equal(DeploymentAnalysisCancellationResult.Cancelled,
            await service.CancelAsync(fixture.OwnerId, fixture.DeploymentId, fixture.AnalysisId));
    }

    /// <summary>
    ///     The production coordinator cancels on uncertain ownership or shutdown, then releases only its own unfinished
    ///     lease.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_coordinator_renews_and_releases_interrupted_processing(bool renewSucceeds)
    {
        using var stopping = new CancellationTokenSource();
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(
            () => throw new InvalidOperationException("Token-aware fixture required."),
            tokenResponse: async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return AnalysisResultTests.Valid();
            }, decorateQueue: queue => new RenewalProbe(queue, renewSucceeds, renewed));
        var queue = fixture.Services.GetRequiredService<IDeploymentAnalysisQueue>();
        var claim = (await queue.ClaimNextAsync())!;
        // Accelerate the coordinator's first heartbeat without altering the persisted 120-second ownership lease.
        var schedulerClaim = claim with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(3) };
        var worker = new DeploymentAnalysisWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Services.GetRequiredService<ILogger<DeploymentAnalysisWorker>>(), TimeProvider.System,
            fixture.Services.GetRequiredService<IOptions<AiAnalysisOptions>>());
        var processing = worker.RunLeasedAsync(fixture.Services, schedulerClaim, stopping.Token);
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (renewSucceeds) await stopping.CancelAsync();
        if (renewSucceeds)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                processing.WaitAsync(TimeSpan.FromSeconds(5)));
        else
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        var persisted = await fixture.Db.DeploymentAnalysisWorkItems.AsNoTracking().SingleAsync();
        Assert.Null(persisted.LeaseId);
        Assert.Null(persisted.CompletedAt);
        Assert.Null((await fixture.Db.AiDeploymentAnalyses.AsNoTracking().SingleAsync()).Summary);
        Assert.Equal(2, (await queue.ClaimNextAsync())!.Attempt);
    }

    /// <summary>Injects a failure at the second cancellation write so transaction rollback is exercised.</summary>
    private sealed class RetirementFailure : DbCommandInterceptor
    {
        /// <summary>Whether queue retirement updates fail.</summary>
        public bool Fail { get; set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.StartsWith("UPDATE ", StringComparison.Ordinal) &&
                (command.CommandText.Contains("DeploymentAnalysisWorkItems", StringComparison.Ordinal) ||
                 command.CommandText.Contains("deployment_analysis_work_items", StringComparison.Ordinal)))
                throw new InvalidOperationException("Synthetic queue retirement failure.");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Scopes share only probe state; actual lease mutations still go through the production queue.</summary>
    private sealed class RenewalProbe(IDeploymentAnalysisQueue inner, bool success, TaskCompletionSource renewed)
        : IDeploymentAnalysisQueue
    {
        /// <inheritdoc />
        public Task<Application.Abstractions.Ai.DeploymentAnalysisWorkItem?> ClaimNextAsync(
            CancellationToken token = default)
        {
            return inner.ClaimNextAsync(token);
        }

        /// <inheritdoc />
        public async Task<bool> RenewAsync(Application.Abstractions.Ai.DeploymentAnalysisWorkItem work,
            CancellationToken token = default)
        {
            var owned = success && await inner.RenewAsync(work, token);
            renewed.TrySetResult();
            return owned;
        }

        /// <inheritdoc />
        public Task<bool> ReleaseAsync(Application.Abstractions.Ai.DeploymentAnalysisWorkItem work,
            CancellationToken token = default)
        {
            return inner.ReleaseAsync(work, token);
        }
    }

    /// <summary>Captures actual worker state and ambient ILogger scopes for privacy assertions.</summary>
    private sealed class AuditLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        /// <summary>Framework scope provider.</summary>
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        /// <summary>Independent snapshots of logged attributes and scopes.</summary>
        public List<Dictionary<string, object?>> Entries { get; } = [];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return new AuditLogger(this);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }

        /// <inheritdoc />
        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopes = scopeProvider;
        }

        /// <summary>Captures state without relying on rendered message strings.</summary>
        private sealed class AuditLogger(AuditLoggerProvider provider) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return provider._scopes.Push(state);
            }

            /// <inheritdoc />
            public bool IsEnabled(LogLevel level)
            {
                return true;
            }

            /// <inheritdoc />
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Assert.Null(exception);
                var attributes = new Dictionary<string, object?>();
                provider._scopes.ForEachScope((scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                        foreach (var pair in values)
                            target[pair.Key] = pair.Value;
                }, attributes);
                if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                    foreach (var pair in fields)
                        attributes[pair.Key] = pair.Value;
                provider.Entries.Add(attributes);
            }
        }
    }

    /// <summary>Owns an isolated SQLite metadata store and fake provider/context ports.</summary>
    private sealed class Fixture(
        SqliteConnection connection,
        ServiceProvider services,
        AutoMateDbContext db,
        Guid owner,
        Guid deployment,
        Guid analysis) : IAsyncDisposable
    {
        /// <summary>Metadata context.</summary>
        public AutoMateDbContext Db { get; } = db;

        /// <summary>Worker dependencies.</summary>
        public ServiceProvider Services { get; } = services;

        /// <summary>Authorized project owner.</summary>
        public Guid OwnerId { get; } = owner;

        /// <summary>Deployment under analysis.</summary>
        public Guid DeploymentId { get; } = deployment;

        /// <summary>Persisted queued analysis.</summary>
        public Guid AnalysisId { get; } = analysis;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        /// <summary>Processes the fixture only after an actual durable claim; expired/terminal analyses remain untouched.</summary>
        public async Task ProcessAsync(CancellationToken token = default)
        {
            var work = await Services.GetRequiredService<IDeploymentAnalysisQueue>().ClaimNextAsync(token);
            if (work is not null) await DeploymentAnalysisWorker.ProcessAsync(Services, work, token);
        }

        /// <summary>Seeds deployment/analysis metadata only; diagnostic payload reads cross a fake store port.</summary>
        public static async Task<Fixture> CreateAsync(Func<Task<LlmAnalysisResponse>> respond,
            ILoggerProvider? audit = null,
            Func<CancellationToken, Task<LlmAnalysisResponse>>? tokenResponse = null,
            Func<IDeploymentAnalysisQueue, IDeploymentAnalysisQueue>? decorateQueue = null,
            DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbOptions = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection);
            if (interceptor is not null) dbOptions.AddInterceptors(interceptor);
            var db = new AnalysisContext(dbOptions.Options, new EphemeralDataProtectionProvider());
            await db.Database.EnsureCreatedAsync();
            var owner = Guid.NewGuid();
            var deployment = new Deployment
            {
                CsProject = new CsProject
                {
                    Name = "web",
                    Path = "web.csproj",
                    Configuration = new Configuration { DotNetVersion = "net10.0", AiDiagnosticEgressConsented = true },
                    Application = new Domain.Entities.Application
                    {
                        Name = "sample",
                        SourcePathOrUrl = "C:/sample",
                        SourceType = SourceType.Local,
                        User = new LocalUser { Id = owner, Username = "test", Email = "test@example.invalid" }
                    }
                }
            };
            var analysis = new AiDeploymentAnalysis
            {
                Deployment = deployment,
                Provider = "openai",
                Model = "gpt-5-mini",
                Status = AiAnalysisStatus.Queued,
                Trigger = AiAnalysisTrigger.Manual,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(90)
            };
            db.AiDeploymentAnalyses.Add(analysis);
            db.DeploymentAnalysisWorkItems.Add(new DeploymentAnalysisWorkItem { Analysis = analysis });
            await db.SaveChangesAsync();
            var context = DispatchProxy.Create<IDeploymentAnalysisContextBuilder, ContextProxy>();
            var services = new ServiceCollection().AddLogging(logging =>
                {
                    if (audit is not null) logging.AddProvider(audit);
                }).AddSingleton<AutoMateDbContext>(db).AddSingleton(TimeProvider.System)
                .AddSingleton<IDiagnosticRedactor>(new DiagnosticRedactor())
                .AddSingleton<IAnalysisResultValidator>(AnalysisResultTests.Validator())
                .AddSingleton(context).AddSingleton<ILlmAnalysisProvider>(new Provider(respond, tokenResponse))
                .AddSingleton(Options.Create(AnalysisEgressPolicyTests.Approved(owner)))
                .AddSingleton<IOptionsMonitor<AiAnalysisOptions>>(
                    new AnalysisEgressPolicyTests.Monitor(AnalysisEgressPolicyTests.Approved(owner)))
                .AddScoped<IAnalysisEgressAuthorizer, AnalysisEgressAuthorizer>()
                .AddScoped<IDeploymentAnalysisQueue>(services =>
                {
                    var queue = new DeploymentAnalysisQueue(services.GetRequiredService<AutoMateDbContext>(),
                        services.GetRequiredService<TimeProvider>(),
                        services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>());
                    return decorateQueue?.Invoke(queue) ?? queue;
                })
                .AddScoped<IDeploymentAnalysisService, DeploymentAnalysisService>().BuildServiceProvider();
            return new Fixture(connection, services, db, owner, deployment.Id, analysis.Id);
        }
    }

    /// <summary>Supplies only an in-memory redacted context; unexpected storage calls fail the test.</summary>
    public class ContextProxy : DispatchProxy
    {
        /// <summary>Optional test hook runs during context construction.</summary>
        public Func<Task<DeploymentAnalysisContext>>? Build { get; set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name == "BuildAsync"
                ? Build?.Invoke() ?? Task.FromResult(new DeploymentAnalysisContext("redacted startup diagnostics",
                    ["order:1", "container/web startup"]))
                : throw new NotSupportedException();
        }
    }

    /// <summary>Strict fake history/query port used to verify actual builder query arguments.</summary>
    public class ReadPort : DispatchProxy
    {
        /// <summary>Test-specific response callback.</summary>
        public Func<MethodInfo?, object?[]?, object?> Call { get; set; } = null!;

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return Call(method, args);
        }
    }

    /// <summary>Provider fixture that can simulate any completion or safe/unsafe failure.</summary>
    private sealed class Provider(
        Func<Task<LlmAnalysisResponse>> respond,
        Func<CancellationToken, Task<LlmAnalysisResponse>>? tokenResponse = null) : ILlmAnalysisProvider
    {
        /// <inheritdoc />
        public Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            return tokenResponse?.Invoke(cancellationToken) ?? respond();
        }
    }

    /// <summary>Converts chronological analysis reads to SQLite-compatible tick ordering.</summary>
    private sealed class AnalysisContext(
        DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider protection) : AutoMateDbContext(options, protection)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<AiDeploymentAnalysis>().Property(e => e.ExpiresAt)
                .HasConversion(e => e.UtcTicks, e => new DateTimeOffset(e, TimeSpan.Zero));
            builder.Entity<DeploymentAnalysisWorkItem>().Property(e => e.NextAttemptAt)
                .HasConversion(e => e.HasValue ? e.Value.UtcTicks : (long?)null,
                    e => e.HasValue ? new DateTimeOffset(e.Value, TimeSpan.Zero) : null);
            builder.Entity<DeploymentAnalysisWorkItem>().Property(e => e.LeaseUntil)
                .HasConversion(e => e.HasValue ? e.Value.UtcTicks : (long?)null,
                    e => e.HasValue ? new DateTimeOffset(e.Value, TimeSpan.Zero) : null);
            builder.Entity<DeploymentAnalysisWorkItem>().Property(e => e.CreatedAt)
                .HasConversion(e => e.UtcTicks, e => new DateTimeOffset(e, TimeSpan.Zero));
            builder.Entity<AiDeploymentAnalysis>().Property(e => e.CreatedAt)
                .HasConversion(e => e.UtcTicks, e => new DateTimeOffset(e, TimeSpan.Zero));
        }
    }
}