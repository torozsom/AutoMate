using System.Reflection;
using System.Text.Json;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Ai.Policy;
using Domain.Entities.Deployments;
using Domain.Entities.Diagnostics;
using Domain.Entities.Projects;
using Domain.Entities.Users;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Infrastructure.Ai.Context;
using Infrastructure.ApplicationServices.Data.Apps;
using Infrastructure.Data;
using Infrastructure.Diagnostics.Archive;
using Infrastructure.Diagnostics.Metrics;
using Infrastructure.Diagnostics.Policy;
using Infrastructure.Diagnostics.Runtime;
using Infrastructure.Diagnostics.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Test.Infra.Diagnostics.PolicyAndMetrics;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Infra.Diagnostics.Archive;

/// <summary>
///     Verifies private-gateway history, aggregation, authorization and cleanup using controlled
///     metadata and provider fixtures.
/// </summary>
/// <exception cref="IOException">The returned task faults when the fixture failure is selected.</exception>
public sealed class SaaSTelemetryTests
{
    /// <summary>
    ///     Reopening a deployment restores its finite lifecycle channels even after runtime output fills a page.
    /// </summary>
    /// <returns>A task completing after durable archive recovery and filtered history assertions.</returns>
    [Fact]
    public async Task Runtime_tail_does_not_hide_saved_build_and_workflow_history()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deploymentForCallbacks = fixture.Deployment;
        var path = Path.Combine(Path.GetTempPath(), "automate-channel-replay-" + Guid.NewGuid().ToString("N"));
        var diskOptions = Options.Create(new DiskSpoolOptions { Directory = path });

        try
        {
            using (var writer = new DiskDeploymentArchive(diskOptions, new DiagnosticRedactor()))
            {
                for (var order = 1; order <= 16; order++)
                {
                    var channel = order <= 3 ? "build" : order <= 6 ? "github-actions" : "web";
                    var e = fixture.Event() with { Message = $"{channel} observation {order}" };

                    await writer.AppendAsync(new DeploymentLogEnvelope(e.EventId!.Value, fixture.User, order,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), e, channel), CancellationToken.None);
                }
            }

            using var archive = new DiskDeploymentArchive(diskOptions, new DiagnosticRedactor());
            var options = Options.Create(new TelemetryStorageOptions { Backend = "LokiMimir" });
            var store = new DeploymentTelemetryStore(fixture.Db,
                new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()), new EmptyLogs(), options,
                new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance,
                new DeploymentRuntimeViewers(TimeProvider.System), archive: archive);
            var replay = await store.ReadRecentAsync(fixture.Project, deploymentForCallbacks, 6);

            Assert.Contains(replay.Events, e => e.TerminalChannel == "build");
            Assert.Contains(replay.Events, e => e.TerminalChannel == "github-actions");
            Assert.Contains(replay.Events, e => e.TerminalChannel == "web");
            Assert.InRange(replay.Events.Count, 1, 6);
            Assert.True(replay.EarlierOmitted);
            Assert.Equal(replay.Events.OrderBy(e => e.OrderId), replay.Events);

            var history = new DeploymentHistoryService(fixture.Db, store, new EmptyMetrics(), options);

            foreach (var channel in new[] { "build", "github-actions" })
            {
                var page = await history.ReadLogsV2Async(fixture.User, fixture.Project, deploymentForCallbacks,
                    null, true, 2, channel: channel);

                Assert.Equal(2, page.Events.Count);
                Assert.All(page.Events, e => Assert.Equal(channel, e.TerminalChannel));
                Assert.NotNull(page.EarlierCursor);

                var earlier = await history.ReadLogsV2Async(fixture.User, fixture.Project, deploymentForCallbacks,
                    page.EarlierCursor, true, 2, channel: channel);

                Assert.Single(earlier.Events);
                Assert.True(earlier.Events[0].OrderId < page.Events[0].OrderId);
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.ReadLogsV2Async(Guid.NewGuid(),
                fixture.Project, deploymentForCallbacks, null, true, 2, channel: "build"));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>
    ///     History applies current masking to old/provider records while retaining durable ordering and
    ///     authorization.
    /// </summary>
    /// <param name="source">Synthetic diagnostic or project source selected by the scenario.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("legacy-recent")]
    [InlineData("legacy-forward")]
    [InlineData("loki")]
    public async Task Historical_payloads_are_redacted_on_read_without_rewriting_storage(string source)
    {
        await using var fixture = await Fixture.CreateAsync();
        var logs = new EmptyLogs();

        if (source == "loki")
        {
            var e = fixture.Event() with
            {
                Message = "{\"api-key\":\"private-value\"}",
                TraceId = "1234567890abcdef1234567890abcdef",
                SpanId = "1234567890abcdef",
                Sequence = 42
            };

            logs.Events =
            [
                new DeploymentLogEnvelope(e.EventId!.Value, fixture.User, 17,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), e, "web")
            ];
        }
        else
        {
            fixture.Db.DeploymentDiagnosticRecords.Add(new DeploymentDiagnosticRecord
            {
                ProjectId = fixture.Project,
                DeploymentId = fixture.Deployment,
                OrderId = 17,
                TimestampUtc = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                Source = "DockerContainer",
                Kind = "Log",
                Severity = "Information",
                TraceId = "1234567890abcdef1234567890abcdef",
                SpanId = "1234567890abcdef",
                Sequence = 42,
                TerminalChannel = "web",
                Message = "{\"api-key\":\"private-value\"}"
            });
            await fixture.Db.SaveChangesAsync();
        }

        var options = Options.Create(source == "loki"
            ? new TelemetryStorageOptions { Backend = "LokiMimir" }
            : new TelemetryStorageOptions());
        var store = new DeploymentTelemetryStore(fixture.Db,
            new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()), logs, options,
            new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance,
            new DeploymentRuntimeViewers(TimeProvider.System));
        var history = new DeploymentHistoryService(fixture.Db, store, new EmptyMetrics(), options);
        var page = await history.ReadLogsAsync(fixture.User, fixture.Project, fixture.Deployment,
            source == "legacy-forward" ? 1 : 0, source != "legacy-forward", 500);

        var delivered = Assert.Single(page.Events);
        Assert.Equal(17, delivered.OrderId);
        Assert.Equal("web", delivered.TerminalChannel);
        Assert.DoesNotContain("private-value", delivered.Message);
        Assert.Contains("[REDACTED]", delivered.Message);
        Assert.NotNull(delivered.TimestampUtc);
        Assert.Equal(DeploymentDiagnosticSeverity.Information, delivered.Severity);
        Assert.Equal("1234567890abcdef1234567890abcdef", delivered.TraceId);
        Assert.Equal(42, delivered.Sequence);

        if (source != "loki")
            Assert.Contains("private-value", (await fixture.Db.DeploymentDiagnosticRecords.SingleAsync()).Message);
        else
            Assert.Empty(await fixture.Db.DeploymentDiagnosticRecords.ToListAsync());
    }

    /// <summary>
    ///     Imported legacy identities merge once and the archive can page during an operational-store
    ///     outage.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Archived_legacy_output_deduplicates_and_survives_backend_failure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = new DeploymentDiagnosticRecord
        {
            ProjectId = fixture.Project,
            DeploymentId = fixture.Deployment,
            OrderId = 17,
            TimestampUtc = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            Source = "DockerContainer",
            Kind = "Log",
            Severity = "Information",
            TerminalChannel = "web",
            Message = "saved"
        };

        fixture.Db.DeploymentDiagnosticRecords.Add(row);
        await fixture.Db.SaveChangesAsync();
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-merge-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var archive = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());
            var e = fixture.Event() with { EventId = row.Id, Message = "saved" };

            await archive.AppendAsync(
                new DeploymentLogEnvelope(row.Id, fixture.User, 17, row.TimestampUtc, row.ExpiresAt, e, "web"),
                CancellationToken.None);
            var options = Options.Create(new TelemetryStorageOptions { Backend = "LokiMimir" });
            var logs = new EmptyLogs { Fail = true };
            var store = new DeploymentTelemetryStore(fixture.Db,
                new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()),
                logs, options, new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance,
                new DeploymentRuntimeViewers(TimeProvider.System), archive: archive);
            var page = await store.ReadPageAsync(fixture.Project, fixture.Deployment, 0, true, 1,
                CancellationToken.None);

            Assert.Single(page.Events);
            Assert.Equal(row.Id, page.Events[0].EventId);
            Assert.True(page.CanAdvanceCursor);
            Assert.Contains("Saved archive output", page.Availability);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>
    ///     Metrics-only historical analysis uses the selected deployment activity window beyond
    ///     operational retention.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Historical_ai_context_uses_archived_metrics_from_its_own_activity_window()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deploymentForCallbacks = fixture.Deployment;
        // A valid timestamp deliberately contains "999"; isolation must inspect numeric signals, not raw JSON text.
        var end = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-120), TimeSpan.Zero).AddMilliseconds(999);

        await fixture.Db.Deployments
            .Where(d => d.Id == deploymentForCallbacks)
            .ExecuteUpdateAsync(update => update
                .SetProperty(d => d.CreatedAt, end.AddHours(-2)).SetProperty(d => d.UpdatedAt, end)
                .SetProperty(d => d.Status, DeploymentStatus.Stopped));
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-context-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var archive = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());

            await archive.ImportMetricsAsync(new ArchiveMetricImport(fixture.User, fixture.Project,
                    deploymentForCallbacks,
                    [
                        new DeploymentMetricPoint("web", "automate_cpu_usage_cores", "cores", end.AddMinutes(-5), 2, 1,
                            3)
                    ]),
                CancellationToken.None);
            await archive.ImportMetricsAsync(new ArchiveMetricImport(fixture.User, fixture.Project, Guid.NewGuid(),
            [
                new DeploymentMetricPoint("web", "automate_cpu_usage_cores", "cores", end.AddMinutes(-5), 999, 999, 999)
            ]), CancellationToken.None);
            var context = await new DeploymentAnalysisContextBuilder(fixture.Db,
                new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()), new EmptyMetrics(),
                new DiagnosticRedactor(),
                Options.Create(new AiAnalysisOptions()), Options.Create(new TelemetryStorageOptions()),
                TimeProvider.System, archive).BuildAsync(deploymentForCallbacks);

            Assert.NotEmpty(context.Text);
            Assert.Contains(context.EvidenceReferences, value => value.StartsWith("metric:automate_cpu_usage_cores:"));
            using var document = JsonDocument.Parse(context.Text);
            var signal = Assert.Single(document.RootElement.GetProperty("metricSignals").EnumerateArray());
            Assert.Equal("automate_cpu_usage_cores", signal.GetProperty("name").GetString());
            Assert.Equal(2, signal.GetProperty("average").GetDouble());
            Assert.Equal(1, signal.GetProperty("minimum").GetDouble());
            Assert.Equal(3, signal.GetProperty("maximum").GetDouble());
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>
    ///     Metadata denial is immediate; a failed archive deletion keeps the outbox until a later
    ///     successful retry.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Archive_cleanup_retries_after_project_deletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-cleanup-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var archive = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());
            var e = fixture.Event();

            await archive.AppendAsync(
                new DeploymentLogEnvelope(e.EventId!.Value, fixture.User, 1, e.TimestampUtc, e.TimestampUtc.AddDays(30),
                    e, "web"), CancellationToken.None);
            var apps = new ApplicationService(fixture.Db,
                NullLogger<ApplicationService>.Instance);

            Assert.True(await apps.DeleteAppAsync(fixture.Project, fixture.User));
            Assert.False(await fixture.Db.Applications.AnyAsync(x => x.Id == fixture.Project));
            Assert.Single(await fixture.Db.DeploymentArchiveCleanups.ToArrayAsync());

            var gateway = DispatchProxy.Create<IDeploymentArchive, CleanupArchiveProxy>();
            var proxy = Assert.IsAssignableFrom<CleanupArchiveProxy>(gateway);

            proxy.Archive = archive;
            using var worker = new DeploymentArchiveCleanupWorker(
                fixture.Services.GetRequiredService<IServiceScopeFactory>(), gateway,
                NullLogger<DeploymentArchiveCleanupWorker>.Instance);

            await worker.ProcessOnceAsync(CancellationToken.None);

            Assert.Single(await fixture.Db.DeploymentArchiveCleanups.AsNoTracking().ToArrayAsync());
            Assert.Single(await archive.ReadAsync(fixture.User, fixture.Project, fixture.Deployment, 0, true, 10, null,
                CancellationToken.None));

            proxy.Fail = false;
            await worker.ProcessOnceAsync(CancellationToken.None);

            Assert.Empty(await fixture.Db.DeploymentArchiveCleanups.AsNoTracking().ToArrayAsync());
            Assert.Empty(await archive.ReadAsync(fixture.User, fixture.Project, fixture.Deployment, 0, true, 10, null,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>
    ///     Backfill checkpoints resume bounded legacy pages after restart and an unavailable log
    ///     backend.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Backfill_resumes_without_rewriting_or_duplicating_legacy_records()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deploymentForCallbacks = fixture.Deployment;

        fixture.Db.DeploymentDiagnosticRecords.AddRange(Enumerable.Range(1, 1001).Select(i =>
            new DeploymentDiagnosticRecord
            {
                ProjectId = fixture.Project,
                DeploymentId = deploymentForCallbacks,
                OrderId = i,
                TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                Source = "DockerContainer",
                Kind = "Log",
                Severity = "Information",
                TerminalChannel = "web",
                Message = "saved-" + i
            }));
        await fixture.Db.SaveChangesAsync();
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-backfill-" + Guid.NewGuid().ToString("N"));

        try
        {
            var logs = new EmptyLogs { Fail = true };
            using var services = new ServiceCollection().AddScoped(fixture.CreateDb)
                .AddSingleton<IDeploymentLogQuery>(logs).AddSingleton<IDeploymentMetricQuery>(new EmptyMetrics())
                .BuildServiceProvider();
            using var archive = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());

            // <summary>
            //     Creates the configured worker instance over fixture-owned dependencies.
            // </summary>
            // <returns>The configured deployment archive backfill worker instance or response for this scenario.</returns>
            DeploymentArchiveBackfillWorker Worker()
            {
                return new DeploymentArchiveBackfillWorker(services.GetRequiredService<IServiceScopeFactory>(), archive,
                    Options.Create(new DiskSpoolOptions { Directory = path }),
                    Options.Create(new TelemetryStorageOptions()),
                    new BackfillTestLogger());
            }

            using (var first = Worker())
            {
                await first.ImportBatchAsync(CancellationToken.None);
            }

            Assert.Equal(500,
                (await archive.ReadAsync(fixture.User, fixture.Project, deploymentForCallbacks, 0, true, 2001, null,
                    CancellationToken.None)).Count);

            using (var restarted = Worker())
            {
                await restarted.ImportBatchAsync(CancellationToken.None);
                logs.Fail = false;
                await restarted.ImportBatchAsync(CancellationToken.None);
                await restarted.ImportBatchAsync(CancellationToken.None);
            }

            var rows = await archive.ReadAsync(fixture.User, fixture.Project, deploymentForCallbacks, 0, true, 2001,
                null,
                CancellationToken.None);

            Assert.Equal(1001, rows.Count);
            Assert.Equal(1001, rows.Select(r => r.EventId).Distinct().Count());
            Assert.Equal(1001, await fixture.Db.DeploymentDiagnosticRecords.CountAsync());
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>
    ///     Neither legacy backend nor outbox mode allows new payload rows when called directly.
    /// </summary>
    /// <param name="specialized">Whether to select the specialized storage mode.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_legacy_backend_modes_cannot_create_new_diagnostics(bool specialized)
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = specialized ? TelemetryStorageTests.Specialized() : new TelemetryStorageOptions();
        var store = new DeploymentTelemetryStore(fixture.Db,
            new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()),
            new EmptyLogs(), Options.Create(settings), new DiagnosticRedactor(),
            NullLogger<DeploymentTelemetryStore>.Instance,
            new DeploymentRuntimeViewers(TimeProvider.System));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistAsync(fixture.Event(), "password=private-channel"));

        Assert.Empty(await fixture.Db.DeploymentDiagnosticRecords.ToListAsync());
        Assert.Empty(await fixture.Db.TelemetryTenantStates.ToListAsync());
    }

    /// <summary>
    ///     Disk receipt success/failure never creates a PostgreSQL diagnostic fallback.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Disk_gateway_persistence_never_inserts_diagnostic_rows_and_failure_never_falls_back()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gateway = new RecordingGateway();
        var options =
            Options.Create(new TelemetryStorageOptions { Backend = "LokiMimir", DeliveryMode = "DiskGateway" });
        using var policies =
            new TelemetryProjectPolicyCache(fixture.Services.GetRequiredService<IServiceScopeFactory>());
        var store = new DeploymentTelemetryStore(fixture.Db,
            new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()), new EmptyLogs(),
            options,
            new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance,
            new DeploymentRuntimeViewers(TimeProvider.System), gateway, policies);
        var e = fixture.Event();
        var policy =
            Assert.IsType<TelemetryProjectPolicy>(await policies.GetAsync(fixture.Project, CancellationToken.None));
        Assert.Equal(fixture.User, policy.UserId);
        Assert.True(policy.RuntimeDiagnosticsEnabled);
        Assert.True(policy.ManagedTelemetryConsent);

        Assert.True(await store.PersistAsync(e, "web") > 0);
        Assert.Equal("password=[REDACTED]", gateway.Events.Single().Message);
        Assert.True(await store.PersistAsync(e with
        {
            EventId = Guid.NewGuid(),
            Kind = DeploymentDiagnosticKind.Metric,
            TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, "web"),
            Metrics = [new DeploymentMetricSample("automate_cpu_usage_cores", 1.5, "cores")]
        }, null) > 0);
        Assert.Equal(2, gateway.Events.Count);
        Assert.Empty(await fixture.Db.DeploymentDiagnosticRecords.ToListAsync());

        gateway.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.PersistAsync(e with { EventId = Guid.NewGuid() }, "web"));

        Assert.Empty(await fixture.Db.DeploymentDiagnosticRecords.ToListAsync());
    }

    /// <summary>
    ///     Foreign ownership is denied before provider history or analytics queries.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Cross_owner_history_and_analytics_are_denied_before_provider_queries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deploymentForCallbacks = fixture.Deployment;
        var options = Options.Create(new TelemetryStorageOptions());
        var logs = new EmptyLogs();
        var store = new DeploymentTelemetryStore(fixture.Db,
            new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()), logs, options,
            new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance,
            new DeploymentRuntimeViewers(TimeProvider.System));
        var history = new DeploymentHistoryService(fixture.Db, store, new EmptyMetrics(), options);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.ReadLogsV2Async(Guid.NewGuid(),
            fixture.Project,
            deploymentForCallbacks, null, true, 500));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ProjectTelemetryAnalyticsService(fixture.Db, new DiagnosticRedactor())
                .ReadAsync(Guid.NewGuid(), fixture.Project, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow));

        Assert.Equal(0, logs.Reads);
    }

    /// <summary>
    ///     Daily calculations and expiry survive label masking and safe legacy readback.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Aggregation_replaces_daily_rows_keeps_counts_and_removes_expired_summaries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var day = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        fixture.Db.DeploymentDailyTelemetry.Add(new DeploymentDailyTelemetry
        {
            UserId = fixture.User,
            ProjectId = fixture.Project,
            DeploymentId = fixture.Deployment,
            DayUtc = day.AddDays(-366),
            Metric = "expired",
            Container = "web"
        });
        await fixture.Db.SaveChangesAsync();
        var daily = new DailyQuery();

        await fixture.Services.DisposeAsync();
        fixture.Services = new ServiceCollection().AddScoped(fixture.CreateDb)
            .AddSingleton<IDailyDeploymentMetricQuery>(daily).AddSingleton<IDeploymentErrorCountQuery>(daily)
            .BuildServiceProvider();
        var worker = new TelemetryDailyAggregationWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TelemetryStorageOptions()), NullLogger<TelemetryDailyAggregationWorker>.Instance,
            new DiagnosticRedactor());

        await worker.AggregateOnceAsync(CancellationToken.None);
        daily.Sum = 25;
        await worker.AggregateOnceAsync(CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var rows = await fixture.Db.DeploymentDailyTelemetry.ToListAsync();

        Assert.Contains(rows, r => r.Metric == "expired");
        var cpu = Assert.Single(rows, r => r.Metric == "automate_cpu_usage_cores" && r.DayUtc == day);
        Assert.Equal(10, cpu.SampleCount);
        Assert.Equal(25, cpu.Sum);
        Assert.Equal("password=[REDACTED]", cpu.Container);

        cpu.Container = "password=legacy-private-label";
        await fixture.Db.SaveChangesAsync();
        var analytics = await new ProjectTelemetryAnalyticsService(fixture.Db, new DiagnosticRedactor()).ReadAsync(
            fixture.User, fixture.Project,
            day.AddDays(-7), DateTimeOffset.UtcNow);

        Assert.DoesNotContain("legacy-private-label", JsonSerializer.Serialize(analytics));
        Assert.Equal("password=legacy-private-label", cpu.Container);
        Assert.Equal(2.5, analytics.Daily.Single(d => d.Metric == cpu.Metric && d.DayUtc == day).Average);
        Assert.True(analytics.Daily.Single(d => d.Metric == "observed_log_errors" && d.DayUtc == day).Incomplete);
    }

    /// <summary>
    ///     Owns an in-memory SQLite database and scoped service provider for telemetry scenarios.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>Owned SQLite connection retaining the in-memory fixture database.</summary>
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        /// <summary>Ephemeral data-protection provider for fixture metadata.</summary>
        private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();

        /// <summary>Fixture metadata context.</summary>
        public AutoMateDbContext Db { get; private set; } = null!;

        /// <summary>Owned fixture dependency provider.</summary>
        public ServiceProvider Services { get; set; } = null!;

        /// <summary>Seeded owner identity.</summary>
        public Guid User { get; private set; }

        /// <summary>Seeded project identity.</summary>
        public Guid Project { get; private set; }

        /// <summary>Seeded deployment identity.</summary>
        public Guid Deployment { get; private set; }

        /// <summary>
        ///     Releases owned fixture services, connections and background resources.
        /// </summary>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        /// <summary>Creates a DI-owned context against the fixture's database.</summary>
        public AutoMateDbContext CreateDb(IServiceProvider _)
        {
            return NewDb();
        }

        /// <summary>Creates an independently owned context against the fixture's database.</summary>
        public AutoMateDbContext NewDb()
        {
            return new OrderedSqliteContext(
                new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(_connection).Options, _protector);
        }

        /// <summary>
        ///     Creates and seeds the owned metadata and service fixture.
        /// </summary>
        /// <returns>A task returning a seeded fixture owning its database and dependencies.</returns>
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();

            await f._connection.OpenAsync();
            f.Db = f.NewDb();
            await f.Db.Database.EnsureCreatedAsync();
            var user = new LocalUser { Username = "test", Email = "test@example.invalid" };
            var project = new Domain.Entities.Projects.Application
            {
                Name = "test",
                User = user,
                SourceType = SourceType.Remote,
                SourcePathOrUrl = "https://example.invalid",
                RuntimeDiagnosticsEnabled = true
            };
            var deployment = new Deployment
            {
                CsProject = new CsProject { Name = "web", Path = "web.csproj", Application = project },
                Status = DeploymentStatus.Running
            };

            f.Db.Deployments.Add(deployment);
            await f.Db.SaveChangesAsync();
            f.User = user.Id;
            f.Project = project.Id;
            f.Deployment = deployment.Id;
            f.Services = new ServiceCollection().AddScoped<AutoMateDbContext>(_ => f.NewDb()).BuildServiceProvider();

            return f;
        }

        /// <summary>
        ///     Creates event data with the supplied identities and synthetic scenario values.
        /// </summary>
        /// <returns>The configured deployment diagnostic event instance or response for this scenario.</returns>
        public DeploymentDiagnosticEvent Event()
        {
            return new DeploymentDiagnosticEvent(Project, Deployment, DeploymentDiagnosticSource.DockerContainer,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
                "password=secret",
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"), EventId: Guid.NewGuid());
        }
    }

    /// <summary>
    ///     Represents UTC timestamps as ordered ticks in the SQLite fixture model.
    /// </summary>
    /// <param name="options">EF options for the fixture metadata database.</param>
    /// <param name="protector">Ephemeral fixture data-protection provider.</param>
    private sealed class OrderedSqliteContext(
        DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider protector)
        : AutoMateDbContext(options, protector)
    {
        /// <summary>
        ///     Adapts timestamp storage and metadata mappings for the SQLite fixture model.
        /// </summary>
        /// <param name="model">EF builder receiving the fixture model mappings.</param>
        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            // SQLite test representation only; production uses PostgreSQL timestamptz.
            foreach (var property in model.Model.GetEntityTypes()
                         .SelectMany(e => e.GetProperties())
                         .Where(p =>
                             p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
                property.SetColumnType("INTEGER");
            }
        }
    }

    /// <summary>
    ///     Records accepted synthetic events and optionally injects gateway failure.
    /// </summary>
    private sealed class RecordingGateway : ITelemetryGateway
    {
        /// <summary>Synthetic events captured or supplied by this fixture.</summary>
        public List<DeploymentDiagnosticEvent> Events { get; } = [];

        /// <summary>Whether to inject the fixture failure.</summary>
        public bool Fail { get; set; }

        /// <summary>
        ///     Records or acknowledges the synthetic event and optionally injects the configured gateway
        ///     failure.
        /// </summary>
        /// <param name="e">Browser change or keyboard event.</param>
        /// <param name="channel">Recorded log channel to select.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment log envelope instance or response for this scenario.</returns>
        /// <exception cref="IOException">The fixture injects the configured storage or output failure.</exception>
        public Task<DeploymentLogEnvelope> AcceptAsync(DeploymentDiagnosticEvent e, string? channel,
            CancellationToken token)
        {
            if (Fail) throw new IOException("Unavailable");
            Events.Add(e);

            return Task.FromResult(new DeploymentLogEnvelope(e.EventId!.Value, Guid.NewGuid(),
                DateTimeOffset.UtcNow.UtcTicks,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), e, channel));
        }

        /// <summary>
        ///     Supplies an empty pending history snapshot.
        /// </summary>
        /// <param name="tenant">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deployment">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning empty pending history with no recorded losses.</returns>
        public Task<TelemetryPendingHistory> ReadPendingAsync(Guid tenant, Guid project, Guid deployment,
            CancellationToken token)
        {
            return Task.FromResult(new TelemetryPendingHistory([], 0));
        }
    }

    /// <summary>
    ///     Injects a transient storage failure without changing production cleanup semantics.
    /// </summary>
    /// <exception cref="IOException">The returned task faults when the fixture failure is selected.</exception>
    public class CleanupArchiveProxy : DispatchProxy
    {
        /// <summary>Underlying archive wrapped by the controlled cleanup proxy.</summary>
        public IDeploymentArchive Archive { get; set; } = null!;

        /// <summary>Whether to inject the fixture failure.</summary>
        public bool Fail { get; set; } = true;

        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="arguments">Arguments for the selected fixture port or reflected component member.</param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        /// <exception cref="IOException">The returned task faults when the fixture failure is selected.</exception>
        protected override object Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name != nameof(IDeploymentArchive.DeleteProjectAsync)) throw new NotSupportedException();

            return Fail
                ? Task.FromException(new IOException("Synthetic archive outage"))
                : Archive.DeleteProjectAsync((Guid)arguments![0]!, (Guid)arguments[1]!,
                    (CancellationToken)arguments[2]!);
        }
    }

    /// <summary>
    ///     Fails a fixture on unexpected backfill errors while permitting its deliberate backend
    ///     outage.
    /// </summary>
    private sealed class BackfillTestLogger : ILogger<DeploymentArchiveBackfillWorker>
    {
        /// <summary>
        ///     Returns no scope handle because this fixture does not retain scopes.
        /// </summary>
        /// <param name="state">Synthetic readiness or structured logger state; ignored by this fixture.</param>
        /// <typeparam name="TState">Type of structured logger state.</typeparam>
        /// <returns>Null; this fixture retains no scopes.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        /// <summary>
        ///     Enables every log level so the fixture can observe all emitted records.
        /// </summary>
        /// <param name="level">Reviewed log severity used to select safe output; ignored by this fixture.</param>
        /// <returns>True for every call in this fixture.</returns>
        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        /// <summary>
        ///     Allows the expected injected I/O failure and rejects other backfill errors.
        /// </summary>
        /// <param name="level">Reviewed log severity used to select safe output; ignored by this fixture.</param>
        /// <param name="id">Selected project identity; ignored by this fixture.</param>
        /// <param name="state">Synthetic readiness or structured logger state; ignored by this fixture.</param>
        /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
        /// <param name="formatter">
        ///     Provider log formatter; untrusted rendered text is not exported; ignored by this fixture.
        /// </param>
        /// <typeparam name="TState">Type of structured logger state.</typeparam>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not IOException) throw new InvalidOperationException("Backfill failed", exception);
        }
    }

    /// <summary>
    ///     Returns configured synthetic log records and optionally injects backend failure.
    /// </summary>
    private sealed class EmptyLogs : IDeploymentLogQuery
    {
        /// <summary>Whether to inject the fixture failure.</summary>
        public bool Fail { get; set; }

        /// <summary>Synthetic provider records returned by history privacy scenarios.</summary>
        public IReadOnlyList<DeploymentLogEnvelope> Events { get; set; } = [];

        /// <summary>Number of recorded fixture reads.</summary>
        public int Reads { get; private set; }

        /// <summary>
        ///     Supplies configured synthetic records, observations or completion state for the fixture
        ///     read.
        /// </summary>
        /// <param name="tenant">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deployment">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="cursor">Opaque history position, or null to read the latest page; ignored by this fixture.</param>
        /// <param name="backwards">
        ///     Whether to read earlier records rather than records after the cursor; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="limit">Requested count or configured admission bound; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <param name="start">UTC observation-window start, or null when absent; ignored by this fixture.</param>
        /// <param name="channel">Optional terminal channel, filtered before applying the page limit.</param>
        /// <returns>A task returning the configured synthetic log records.</returns>
        /// <exception cref="IOException">The fixture injects the configured storage or output failure.</exception>
        public Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            long cursor,
            bool backwards, int limit, CancellationToken token, DateTimeOffset? start = null, string? channel = null)
        {
            Reads++;
            if (Fail) throw new IOException("Synthetic backend outage");

            return Task.FromResult(Events);
        }

        /// <summary>
        ///     Reports the configured synthetic backend visibility condition.
        /// </summary>
        /// <param name="events">The bounded batch of redacted diagnostic envelopes; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning true for every fixture call.</returns>
        public Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            return Task.FromResult(true);
        }
    }

    /// <summary>
    ///     Supplies empty metric reads and confirms synthetic delivery visibility.
    /// </summary>
    private sealed class EmptyMetrics : IDeploymentMetricQuery
    {
        /// <summary>
        ///     Supplies configured synthetic records, observations or completion state for the fixture
        ///     read.
        /// </summary>
        /// <param name="tenant">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deployment">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="start">UTC observation-window start; ignored by this fixture.</param>
        /// <param name="end">UTC observation-window end; ignored by this fixture.</param>
        /// <param name="maximumPoints">The maximum number of returned aggregate points; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning an empty synthetic result collection.</returns>
        public Task<IReadOnlyList<DeploymentMetricPoint>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<DeploymentMetricPoint>>([]);
        }

        /// <summary>
        ///     Reports the configured synthetic backend visibility condition.
        /// </summary>
        /// <param name="events">The bounded batch of redacted diagnostic envelopes; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning true for every fixture call.</returns>
        public Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            return Task.FromResult(true);
        }
    }

    /// <summary>
    ///     Supplies fixed daily metrics and error counts for aggregation assertions.
    /// </summary>
    private sealed class DailyQuery : IDailyDeploymentMetricQuery, IDeploymentErrorCountQuery
    {
        /// <summary>Synthetic daily metric sum.</summary>
        public double Sum { get; set; } = 20;

        /// <summary>
        ///     Supplies fixed daily metric aggregates for the fixture.
        /// </summary>
        /// <param name="tenant">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deployment">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="start">UTC observation-window start; ignored by this fixture.</param>
        /// <param name="end">UTC observation-window end; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured synthetic collection or recorded fixture selection.</returns>
        public Task<IReadOnlyList<DailyMetricStatistics>> ReadDailyAsync(Guid tenant, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<DailyMetricStatistics>>([
                new DailyMetricStatistics("password=private-container", "automate_cpu_usage_cores", "cores", 10, Sum, 1,
                    3)
            ]);
        }

        /// <summary>
        ///     Supplies the fixed synthetic error count for daily aggregation.
        /// </summary>
        /// <param name="tenant">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deployment">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="start">UTC observation-window start; ignored by this fixture.</param>
        /// <param name="end">UTC observation-window end; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the fixed synthetic error count of four.</returns>
        public Task<long> CountErrorsAsync(Guid tenant, Guid project, Guid deployment, DateTimeOffset start,
            DateTimeOffset end, CancellationToken token)
        {
            return Task.FromResult(4L);
        }
    }
}
