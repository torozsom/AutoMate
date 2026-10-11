using System.Reflection;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Application.Data.Apps;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

public sealed class SaaSTelemetryTests
{
    /// <summary>History applies current masking to old/provider records while retaining durable ordering and authorization.</summary>
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

    /// <summary>Imported legacy identities merge once and the archive can page during an operational-store outage.</summary>
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
                default);
            var options = Options.Create(new TelemetryStorageOptions { Backend = "LokiMimir" });
            var logs = new EmptyLogs { Fail = true };
            var store = new DeploymentTelemetryStore(fixture.Db,
                new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()),
                logs, options, new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance,
                new DeploymentRuntimeViewers(TimeProvider.System), archive: archive);
            var page = await store.ReadPageAsync(fixture.Project, fixture.Deployment, 0, true, 1, default);
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

    /// <summary>Metrics-only historical analysis uses the selected deployment activity window beyond operational retention.</summary>
    [Fact]
    public async Task Historical_ai_context_uses_archived_metrics_from_its_own_activity_window()
    {
        await using var fixture = await Fixture.CreateAsync();
        var end = DateTimeOffset.UtcNow.AddDays(-120);
        await fixture.Db.Deployments.Where(d => d.Id == fixture.Deployment).ExecuteUpdateAsync(update => update
            .SetProperty(d => d.CreatedAt, end.AddHours(-2)).SetProperty(d => d.UpdatedAt, end)
            .SetProperty(d => d.Status, DeploymentStatus.Stopped));
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());
            await archive.ImportMetricsAsync(new ArchiveMetricImport(fixture.User, fixture.Project, fixture.Deployment,
                [
                    new DeploymentMetricPoint("web", "automate_cpu_usage_cores", "cores", end.AddMinutes(-5), 2, 1, 3)
                ]),
                default);
            await archive.ImportMetricsAsync(new ArchiveMetricImport(fixture.User, fixture.Project, Guid.NewGuid(),
            [
                new DeploymentMetricPoint("web", "automate_cpu_usage_cores", "cores", end.AddMinutes(-5), 999, 999, 999)
            ]), default);
            var context = await new DeploymentAnalysisContextBuilder(fixture.Db,
                new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()), new EmptyMetrics(),
                new DiagnosticRedactor(),
                Options.Create(new AiAnalysisOptions()), Options.Create(new TelemetryStorageOptions()),
                TimeProvider.System, archive).BuildAsync(fixture.Deployment);
            Assert.NotEmpty(context.Text);
            Assert.Contains(context.EvidenceReferences, value => value.StartsWith("metric:automate_cpu_usage_cores:"));
            Assert.DoesNotContain("999", context.Text);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>Metadata denial is immediate; a failed archive deletion keeps the outbox until a later successful retry.</summary>
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
                    e, "web"), default);
            var apps = new ApplicationService(fixture.Db,
                NullLogger<ApplicationService>.Instance);
            Assert.True(await apps.DeleteAppAsync(fixture.Project, fixture.User));
            Assert.False(await fixture.Db.Applications.AnyAsync(x => x.Id == fixture.Project));
            Assert.Single(await fixture.Db.DeploymentArchiveCleanups.ToArrayAsync());
            var gateway = DispatchProxy.Create<IDeploymentArchive, CleanupArchiveProxy>();
            var proxy = (CleanupArchiveProxy)gateway;
            proxy.Archive = archive;
            using var worker = new DeploymentArchiveCleanupWorker(
                fixture.Services.GetRequiredService<IServiceScopeFactory>(), gateway,
                NullLogger<DeploymentArchiveCleanupWorker>.Instance);
            await worker.ProcessOnceAsync(default);
            Assert.Single(await fixture.Db.DeploymentArchiveCleanups.AsNoTracking().ToArrayAsync());
            Assert.Single(await archive.ReadAsync(fixture.User, fixture.Project, fixture.Deployment, 0, true, 10, null,
                default));
            proxy.Fail = false;
            await worker.ProcessOnceAsync(default);
            Assert.Empty(await fixture.Db.DeploymentArchiveCleanups.AsNoTracking().ToArrayAsync());
            Assert.Empty(await archive.ReadAsync(fixture.User, fixture.Project, fixture.Deployment, 0, true, 10, null,
                default));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>Backfill checkpoints resume bounded legacy pages after restart and an unavailable log backend.</summary>
    [Fact]
    public async Task Backfill_resumes_without_rewriting_or_duplicating_legacy_records()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.DeploymentDiagnosticRecords.AddRange(Enumerable.Range(1, 1001).Select(i =>
            new DeploymentDiagnosticRecord
            {
                ProjectId = fixture.Project,
                DeploymentId = fixture.Deployment,
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
            using var services = new ServiceCollection().AddScoped<AutoMateDbContext>(_ => fixture.NewDb())
                .AddSingleton<IDeploymentLogQuery>(logs).AddSingleton<IDeploymentMetricQuery>(new EmptyMetrics())
                .BuildServiceProvider();
            using var archive = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());

            DeploymentArchiveBackfillWorker Worker()
            {
                return new DeploymentArchiveBackfillWorker(services.GetRequiredService<IServiceScopeFactory>(), archive,
                    Options.Create(new DiskSpoolOptions { Directory = path }),
                    Options.Create(new TelemetryStorageOptions()),
                    new BackfillTestLogger());
            }

            using (var first = Worker())
            {
                await first.ImportBatchAsync(default);
            }

            Assert.Equal(500,
                (await archive.ReadAsync(fixture.User, fixture.Project, fixture.Deployment, 0, true, 2001, null,
                    default)).Count);
            using (var restarted = Worker())
            {
                await restarted.ImportBatchAsync(default);
                logs.Fail = false;
                await restarted.ImportBatchAsync(default);
                await restarted.ImportBatchAsync(default);
            }

            var rows = await archive.ReadAsync(fixture.User, fixture.Project, fixture.Deployment, 0, true, 2001, null,
                default);
            Assert.Equal(1001, rows.Count);
            Assert.Equal(1001, rows.Select(r => r.EventId).Distinct().Count());
            Assert.Equal(1001, await fixture.Db.DeploymentDiagnosticRecords.CountAsync());
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>Neither legacy backend nor outbox mode allows new payload rows when called directly.</summary>
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

    /// <summary>Disk receipt success/failure never creates a PostgreSQL diagnostic fallback.</summary>
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

    /// <summary>Foreign ownership is denied before provider history or analytics queries.</summary>
    [Fact]
    public async Task Cross_owner_history_and_analytics_are_denied_before_provider_queries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var options = Options.Create(new TelemetryStorageOptions());
        var logs = new EmptyLogs();
        var store = new DeploymentTelemetryStore(fixture.Db,
            new DeploymentDiagnosticStore(fixture.Db, new DiagnosticRedactor()), logs, options,
            new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance,
            new DeploymentRuntimeViewers(TimeProvider.System));
        var history = new DeploymentHistoryService(fixture.Db, store, new EmptyMetrics(), options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.ReadLogsV2Async(Guid.NewGuid(),
            fixture.Project,
            fixture.Deployment, null, true, 500));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ProjectTelemetryAnalyticsService(fixture.Db, new DiagnosticRedactor())
                .ReadAsync(Guid.NewGuid(), fixture.Project, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow));
        Assert.Equal(0, logs.Reads);
    }

    /// <summary>Daily calculations and expiry survive label masking and safe legacy readback.</summary>
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
        fixture.Services = new ServiceCollection().AddScoped<AutoMateDbContext>(_ => fixture.NewDb())
            .AddSingleton<IDailyDeploymentMetricQuery>(daily).AddSingleton<IDeploymentErrorCountQuery>(daily)
            .BuildServiceProvider();
        var worker = new TelemetryDailyAggregationWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TelemetryStorageOptions()), NullLogger<TelemetryDailyAggregationWorker>.Instance,
            new DiagnosticRedactor());
        await worker.AggregateOnceAsync(default);
        daily.Sum = 25;
        await worker.AggregateOnceAsync(default);
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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
        public AutoMateDbContext Db { get; private set; } = null!;
        public ServiceProvider Services { get; set; } = null!;
        public Guid User { get; private set; }
        public Guid Project { get; private set; }
        public Guid Deployment { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        public AutoMateDbContext NewDb()
        {
            return new OrderedSqliteContext(
                new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(_connection).Options, _protector);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f._connection.OpenAsync();
            f.Db = f.NewDb();
            await f.Db.Database.EnsureCreatedAsync();
            var user = new LocalUser { Username = "test", Email = "test@example.invalid" };
            var project = new Domain.Entities.Application
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

        public DeploymentDiagnosticEvent Event()
        {
            return new DeploymentDiagnosticEvent(Project, Deployment, DeploymentDiagnosticSource.DockerContainer,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
                "password=secret",
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"), EventId: Guid.NewGuid());
        }
    }

    private sealed class OrderedSqliteContext(
        DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider protector)
        : AutoMateDbContext(options, protector)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            // SQLite test representation only; production uses PostgreSQL timestamptz.
            foreach (var property in model.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).Where(p =>
                         p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
                property.SetColumnType("INTEGER");
            }
        }
    }

    private sealed class RecordingGateway : ITelemetryGateway
    {
        public List<DeploymentDiagnosticEvent> Events { get; } = [];
        public bool Fail { get; set; }

        public Task<DeploymentLogEnvelope> AcceptAsync(DeploymentDiagnosticEvent e, string? channel,
            CancellationToken token)
        {
            if (Fail) throw new IOException("Unavailable");
            Events.Add(e);
            return Task.FromResult(new DeploymentLogEnvelope(e.EventId!.Value, Guid.NewGuid(),
                DateTimeOffset.UtcNow.UtcTicks,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), e, channel));
        }

        public Task<TelemetryPendingHistory> ReadPendingAsync(Guid tenant, Guid project, Guid deployment,
            CancellationToken token)
        {
            return Task.FromResult(new TelemetryPendingHistory([], 0));
        }
    }

    /// <summary>Injects a transient storage failure without changing production cleanup semantics.</summary>
    public class CleanupArchiveProxy : DispatchProxy
    {
        public IDeploymentArchive Archive { get; set; } = null!;
        public bool Fail { get; set; } = true;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name != nameof(IDeploymentArchive.DeleteProjectAsync)) throw new NotSupportedException();
            return Fail
                ? Task.FromException(new IOException("Synthetic archive outage"))
                : Archive.DeleteProjectAsync((Guid)arguments![0]!, (Guid)arguments[1]!,
                    (CancellationToken)arguments[2]!);
        }
    }

    /// <summary>Fails a fixture on unexpected backfill errors while permitting its deliberate backend outage.</summary>
    private sealed class BackfillTestLogger : ILogger<DeploymentArchiveBackfillWorker>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not IOException) throw new InvalidOperationException("Backfill failed", exception);
        }
    }

    private sealed class EmptyLogs : IDeploymentLogQuery
    {
        public bool Fail { get; set; }

        /// <summary>Synthetic provider records returned by history privacy scenarios.</summary>
        public IReadOnlyList<DeploymentLogEnvelope> Events { get; set; } = [];

        public int Reads { get; private set; }

        public Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            long cursor,
            bool backwards, int limit, CancellationToken token, DateTimeOffset? start = null)
        {
            Reads++;
            if (Fail) throw new IOException("Synthetic backend outage");
            return Task.FromResult(Events);
        }

        public Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class EmptyMetrics : IDeploymentMetricQuery
    {
        public Task<IReadOnlyList<DeploymentMetricPoint>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<DeploymentMetricPoint>>([]);
        }

        public Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class DailyQuery : IDailyDeploymentMetricQuery, IDeploymentErrorCountQuery
    {
        public double Sum { get; set; } = 20;

        public Task<IReadOnlyList<DailyMetricStatistics>> ReadDailyAsync(Guid tenant, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<DailyMetricStatistics>>([
                new DailyMetricStatistics("password=private-container", "automate_cpu_usage_cores", "cores", 10, Sum, 1,
                    3)
            ]);
        }

        public Task<long> CountErrorsAsync(Guid tenant, Guid project, Guid deployment, DateTimeOffset start,
            DateTimeOffset end, CancellationToken token)
        {
            return Task.FromResult(4L);
        }
    }
}