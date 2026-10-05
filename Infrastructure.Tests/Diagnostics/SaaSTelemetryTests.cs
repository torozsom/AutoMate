using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.DoesNotContain(rows, r => r.Metric == "expired");
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

    private sealed class EmptyLogs : IDeploymentLogQuery
    {
        /// <summary>Synthetic provider records returned by history privacy scenarios.</summary>
        public IReadOnlyList<DeploymentLogEnvelope> Events { get; set; } = [];

        public int Reads { get; private set; }

        public Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            long cursor,
            bool backwards, int limit, CancellationToken token, DateTimeOffset? start = null)
        {
            Reads++;
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