using Application.Abstractions.Diagnostics;
using Application.Data.Apps;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.ApplicationServices.Data.Apps;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Tests.Ai;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.Tests;

/// <summary>Runs production SQL projections in owned disposable PostgreSQL schemas, without provider requests.</summary>
public sealed class WorkspaceQueryTests
{
    /// <summary>Inventory filters precede paging, counts are owner-scoped, and ordering is stable.</summary>
    [AiPostgresFact]
    public async Task Inventory_pages_filters_and_owner_boundaries()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Db();
        var service = new WorkspaceQuery(db, fixture.Clock);
        var first = await service.ProjectsAsync(fixture.Owner, new ProjectInventoryRequest());
        Assert.Equal(23, first.SavedProjects);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(23, first.Total);
        var second = await service.ProjectsAsync(fixture.Owner, new ProjectInventoryRequest(Page: 2));
        Assert.Equal(3, second.Items.Count);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
        var filtered = await service.ProjectsAsync(fixture.Owner,
            new ProjectInventoryRequest("API", SourceType.Remote, DeploymentStatus.Starting, "name", 100));
        var row = Assert.Single(filtered.Items);
        Assert.Equal("API service", row.Name);
        Assert.Equal(1, filtered.Page);
        Assert.Equal(2, row.Components);
        Assert.Equal(1, row.WebApps);
        Assert.Equal(1, (await service.ProjectsAsync(fixture.OtherOwner, new ProjectInventoryRequest())).SavedProjects);
        Assert.Equal(0, (await service.ProjectsAsync(Guid.NewGuid(), new ProjectInventoryRequest())).SavedProjects);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Stopped success, unknown outcomes, missing durations, weighted samples and owner isolation remain accurate.</summary>
    [AiPostgresFact]
    public async Task Overview_preserves_recorded_outcome_and_observation_semantics()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Db();
        var service = new WorkspaceQuery(db, fixture.Clock);
        var result = await service.OverviewAsync(fixture.Owner, 30);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Unknown);
        Assert.Equal(20, result.AverageDurationSeconds);
        Assert.Equal(30, result.Activity.Count);
        Assert.Equal(4, result.Recent.Count);
        var cpu = Assert.Single(result.Resources, r => r.Metric == "automate_cpu_usage_cores");
        Assert.Equal(4, cpu.Samples);
        Assert.Equal(.4, cpu.Average, 8);
        Assert.Equal(.1, cpu.Minimum);
        Assert.Equal(.9, cpu.Maximum);
        Assert.True(cpu.Incomplete);
        Assert.Contains("incomplete", result.ResourceNotice);
        Assert.Contains(result.Attention, item => item.State == "Queued" && item.DeploymentId == null);
        Assert.Contains(result.Attention, item => item.State == "Starting");
        var unknown = Assert.Single(result.Recent, r => r.Project == "No duration");
        Assert.Null(unknown.DurationSeconds);
        var empty = await service.OverviewAsync(Guid.NewGuid(), 7);
        Assert.Empty(empty.Recent);
        Assert.Empty(empty.Resources);
        Assert.Equal(0, empty.Attempts);
        Assert.Null(empty.AverageDurationSeconds);
        var project =
            await new ProjectTelemetryAnalyticsService(db, new DiagnosticRedactor(), clock: fixture.Clock).ReadAsync(
                fixture.Owner,
                fixture.PrimaryProject, result.Start, result.End);
        Assert.Equal(1, project.SuccessfulDeployments);
        Assert.Equal(1, project.FailedDeployments);
    }

    /// <summary>A telemetry table outage leaves deployment activity usable and supplies fixed guidance.</summary>
    [AiPostgresFact]
    public async Task Telemetry_outage_is_isolated_and_invalid_inputs_are_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Db();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE deployment_daily_telemetry");
        var service = new WorkspaceQuery(db, fixture.Clock);
        var result = await service.OverviewAsync(fixture.Owner, 90);
        Assert.Equal(4, result.Attempts);
        Assert.Empty(result.Resources);
        Assert.Contains("temporarily unavailable", result.ResourceNotice);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OverviewAsync(Guid.Empty, 30));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.OverviewAsync(fixture.Owner, 365));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.OverviewAsync(fixture.Owner, 7, new CancellationToken(true)));
    }

    /// <summary>Short windows use archived observations, filter before pagination and reject other owners.</summary>
    [AiPostgresFact]
    public async Task Detailed_metrics_page_containers_and_authorize_private_batches()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Db();
        var path = Path.Combine(Path.GetTempPath(), "automate-range-sql-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var disk = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());
            var settings = Options.Create(new TelemetryStorageOptions());
            var reader = new ArchiveMetricBatchReader(db, disk, settings);
            var port = new BatchPort(reader, disk);
            var service = new MetricExplorationService(db, port, settings, TimeProvider.System);
            var deployment = await db.Deployments.Where(d => d.CsProject!.AppId == fixture.PrimaryProject)
                .OrderBy(d => d.CreatedAt).Select(d => d.Id).FirstAsync();
            var now = DateTimeOffset.UtcNow.AddSeconds(-1);
            var range = new MetricTimeRange(now.AddMinutes(-10), now);
            for (var i = 0; i < 60; i++)
            {
                var id = Guid.NewGuid();
                var time = now.AddMinutes(-2);
                await disk.AppendAsync(new DeploymentLogEnvelope(id, fixture.Owner, i + 1, time, time.AddDays(30),
                    new DeploymentDiagnosticEvent(fixture.PrimaryProject, deployment,
                        DeploymentDiagnosticSource.DockerContainer,
                        DeploymentDiagnosticKind.Metric, DeploymentDiagnosticSeverity.Information, time, "",
                        new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web-" + i),
                        Metrics:
                        [
                            new DeploymentMetricSample("automate_cpu_usage_cores", .5, "cores"),
                            new DeploymentMetricSample("automate_memory_used_bytes", 1024, "bytes")
                        ], EventId: id), "web-" + i), default);
            }

            var first = await service.ReadAsync(
                new MetricExplorationQuery(fixture.Owner, range, fixture.PrimaryProject));
            var second =
                await service.ReadAsync(new MetricExplorationQuery(fixture.Owner, range, fixture.PrimaryProject,
                    Page: 2));
            Assert.Equal(120, first.Total);
            Assert.Equal(25, first.Items.Count);
            Assert.Equal(25, second.Items.Count);
            Assert.Empty(first.Items.Intersect(second.Items));
            Assert.Equal(first.Chart, second.Chart);
            var selected = await service.ReadAsync(new MetricExplorationQuery(fixture.Owner, range,
                fixture.PrimaryProject, Container: "web-59"));
            Assert.Equal(2, selected.Total);
            Assert.All(selected.Items, p => Assert.Equal("web-59", p.Container));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.ReadAsync(new MetricExplorationQuery(fixture.OtherOwner, range, fixture.PrimaryProject)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                reader.ReadAsync(
                    new ArchiveMetricBatchRequest(fixture.OtherOwner, range, fixture.PrimaryProject, deployment),
                    default));
            var overview = await new WorkspaceQuery(db, TimeProvider.System, exploration: service)
                .OverviewAsync(fixture.Owner, range);
            Assert.Equal(60, overview.Resources.Single(p => p.Metric == "automate_cpu_usage_cores").Samples);
            var consent = new MetricExplorationService(db, port,
                Options.Create(new TelemetryStorageOptions
                    { ManagedService = true, ManagedDataProcessingApproved = true }), TimeProvider.System);
            var privateReader = new ArchiveMetricBatchReader(db, disk,
                Options.Create(new TelemetryStorageOptions
                    { ManagedService = true, ManagedDataProcessingApproved = true }));
            Assert.NotEmpty(
                (await privateReader.ReadAsync(
                    new ArchiveMetricBatchRequest(fixture.Owner, range, fixture.PrimaryProject), default)).Items);
            Assert.Null(
                (await privateReader.ReadAsync(
                    new ArchiveMetricBatchRequest(fixture.Owner, range, fixture.PrimaryProject),
                    default)).Availability);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    /// <summary>Partial day boundaries exclude unrelated daily summaries and complete UTC days retain sample weighting.</summary>
    [AiPostgresFact]
    public async Task Long_windows_use_complete_days_without_boundary_double_counting()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Db();
        var day = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-1);
        var deployment = await db.Deployments.Where(d => d.CsProject!.AppId == fixture.PrimaryProject)
            .Select(d => d.Id).FirstAsync();
        db.DeploymentDailyTelemetry.Add(new DeploymentDailyTelemetry
        {
            UserId = fixture.Owner,
            ProjectId = fixture.PrimaryProject,
            DeploymentId = deployment,
            DayUtc = day,
            Container = "web",
            Metric = "automate_cpu_usage_cores",
            Unit = "cores",
            SampleCount = 4,
            Sum = 2,
            Minimum = .1,
            Maximum = .9
        });
        await db.SaveChangesAsync();
        await db.DeploymentDailyTelemetry.Where(d => d.UserId == fixture.OtherOwner)
            .ExecuteUpdateAsync(update => update.SetProperty(d => d.DayUtc, day));
        var path = Path.Combine(Path.GetTempPath(), "automate-day-sql-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var disk = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
                new DiagnosticRedactor());
            var settings = Options.Create(new TelemetryStorageOptions());
            var service = new MetricExplorationService(db,
                new BatchPort(new ArchiveMetricBatchReader(db, disk, settings), disk),
                settings, TimeProvider.System);
            var range = new MetricTimeRange(day.AddDays(-1).AddHours(12), DateTimeOffset.UtcNow.AddSeconds(-1));
            var result =
                await service.ReadAsync(new MetricExplorationQuery(fixture.Owner, range, fixture.PrimaryProject));
            var row = Assert.Single(result.Chart);
            Assert.Equal(day, row.Timestamp);
            Assert.Equal(4, row.Samples);
            Assert.Equal(.5, row.Average);
            // A malformed legacy aggregate cannot disclose another owner's deployment identity or samples.
            Assert.Empty((await service.ReadAsync(new MetricExplorationQuery(fixture.OtherOwner, range))).Chart);

            Assert.DoesNotContain(result.Chart, p => p.Timestamp == day.AddDays(1));
            var offline = new MetricExplorationService(db,
                new BatchPort(new ArchiveMetricBatchReader(db, disk, settings), disk, true),
                settings, TimeProvider.System);
            var partial =
                await offline.ReadAsync(new MetricExplorationQuery(fixture.Owner, range, fixture.PrimaryProject));
            Assert.Equal(4, Assert.Single(partial.Chart).Samples);
            Assert.Contains("unavailable", partial.Availability);

            var fiveYears = new MetricTimeRange(range.End.AddYears(-5), range.End);
            var longResult =
                await service.ReadAsync(new MetricExplorationQuery(fixture.Owner, fiveYears, fixture.PrimaryProject));
            Assert.Equal(4, Assert.Single(longResult.Chart).Samples);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    /// <summary>Private HTTP-equivalent fixture delegates batch authorization to production SQL.</summary>
    private sealed class BatchPort(ArchiveMetricBatchReader reader, IDeploymentArchive disk, bool unavailable = false)
        : IDeploymentArchive
    {
        /// <inheritdoc />
        public Task<ArchiveMetricBatch> ReadMetricBatchAsync(ArchiveMetricBatchRequest request, CancellationToken token)
        {
            return unavailable
                ? Task.FromException<ArchiveMetricBatch>(new HttpRequestException("synthetic outage"))
                : reader.ReadAsync(request, token);
        }

        /// <inheritdoc />
        public Task<DeploymentLogEnvelope> AppendAsync(DeploymentLogEnvelope e, CancellationToken token)
        {
            return disk.AppendAsync(e, token);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            long cursor, bool backwards, int limit, string? search, CancellationToken token)
        {
            return disk.ReadAsync(tenant, project, deployment, cursor, backwards, limit, search, token);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DeploymentMetricPoint>> ReadMetricsAsync(Guid tenant, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token)
        {
            return disk.ReadMetricsAsync(tenant, project, deployment, start, end, maximumPoints, token);
        }

        /// <inheritdoc />
        public Task DeleteProjectAsync(Guid tenant, Guid project, CancellationToken token)
        {
            return disk.DeleteProjectAsync(tenant, project, token);
        }

        /// <inheritdoc />
        public Task ImportMetricsAsync(ArchiveMetricImport import, CancellationToken token)
        {
            return disk.ImportMetricsAsync(import, token);
        }
    }

    /// <summary>Controls UTC windows without mutating application time.</summary>
    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            return value;
        }
    }

    /// <summary>Owns a generated schema only in the explicitly configured isolated verification database.</summary>
    private sealed class Fixture(string connection, string schema) : IAsyncDisposable
    {
        /// <summary>Fixture owner.</summary>
        public Guid Owner { get; private set; }

        /// <summary>Different owner for privacy checks.</summary>
        public Guid OtherOwner { get; private set; }

        /// <summary>Project with multiple component histories.</summary>
        public Guid PrimaryProject { get; private set; }

        /// <summary>Stable current UTC date.</summary>
        public TimeProvider Clock { get; } = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(5));

        /// <summary>Base isolated connection.</summary>
        private string Connection => connection;

        /// <summary>Generated safe schema identity.</summary>
        private string Schema => schema;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await using var db = new NpgsqlConnection(connection);
            await db.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", db);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Independent production EF context.</summary>
        public AutoMateDbContext Db()
        {
            var settings = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false };
            var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseNpgsql(settings.ToString(),
                    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                .UseSnakeCaseNamingConvention()
                .Options;
            return new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        }

        /// <summary>Creates a schema, applies real migrations and seeds public metadata.</summary>
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(Environment.GetEnvironmentVariable("AUTOMATE_AI_TEST_DB")!,
                "ui_test_" + Guid.NewGuid().ToString("N"));
            await using var admin = new NpgsqlConnection(fixture.Connection);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {fixture.Schema}", admin);
            await command.ExecuteNonQueryAsync();
            try
            {
                await using var db = fixture.Db();
                await db.Database.MigrateAsync();
                var owner = new LocalUser { Username = "owner", Email = "owner@example.invalid" };
                var other = new LocalUser { Username = "other", Email = "other@example.invalid" };
                var apps = Enumerable.Range(0, 23).Select(index => new Domain.Entities.Application
                {
                    Name = index == 0 ? "API service" : $"Project {index:00}",
                    User = owner,
                    SourceType = index == 0 ? SourceType.Remote : SourceType.Local,
                    SourcePathOrUrl = "fixture"
                }).ToArray();
                var outsider = new Domain.Entities.Application
                {
                    Name = "Private outsider",
                    User = other,
                    SourceType = SourceType.Local,
                    SourcePathOrUrl = "private"
                };
                var component = new CsProject
                    { Name = "Web", Path = "web.csproj", IsWebProject = true, Application = apps[0] };
                var successful = new Deployment
                    { CsProject = component, Status = DeploymentStatus.Stopped, Outcome = DeploymentOutcome.Succeeded };
                var failed = new Deployment
                    { CsProject = component, Status = DeploymentStatus.Failed, Outcome = DeploymentOutcome.Failed };
                var component2 = new CsProject { Name = "Worker", Path = "worker.csproj", Application = apps[0] };
                var starting = new Deployment { CsProject = component2, Status = DeploymentStatus.Starting };
                apps[1].Name = "No duration";
                var unknown = new Deployment
                {
                    CsProject = new CsProject { Name = "Web", Path = "app.csproj", Application = apps[1] },
                    Status = DeploymentStatus.Stopped
                };
                db.Applications.AddRange(apps.Append(outsider));
                db.Deployments.AddRange(successful, failed, starting, unknown);
                await db.SaveChangesAsync();
                var now = DateTimeOffset.UtcNow;
                await db.Deployments.Where(d => d.Id == successful.Id).ExecuteUpdateAsync(u =>
                    u.SetProperty(d => d.CreatedAt, now.AddMinutes(-10))
                        .SetProperty(d => d.FinishedAt, now.AddMinutes(-10).AddSeconds(10)));
                await db.Deployments.Where(d => d.Id == failed.Id).ExecuteUpdateAsync(u =>
                    u.SetProperty(d => d.CreatedAt, now.AddMinutes(-20))
                        .SetProperty(d => d.FinishedAt, now.AddMinutes(-20).AddSeconds(30)));
                await db.Deployments.Where(d => d.Id == starting.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.CreatedAt, now.AddMinutes(-5)));
                fixture.Owner = owner.Id;
                fixture.OtherOwner = other.Id;
                fixture.PrimaryProject = apps[0].Id;
                var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
                db.DeploymentDailyTelemetry.AddRange(
                    new DeploymentDailyTelemetry
                    {
                        UserId = owner.Id,
                        ProjectId = apps[0].Id,
                        DeploymentId = successful.Id,
                        DayUtc = day,
                        Container = "web",
                        Metric = "automate_cpu_usage_cores",
                        Unit = "cores",
                        SampleCount = 1,
                        Sum = .1,
                        Minimum = .1,
                        Maximum = .1
                    },
                    new DeploymentDailyTelemetry
                    {
                        UserId = owner.Id,
                        ProjectId = apps[0].Id,
                        DeploymentId = starting.Id,
                        DayUtc = day,
                        Container = "worker",
                        Metric = "automate_cpu_usage_cores",
                        Unit = "cores",
                        SampleCount = 3,
                        Sum = 1.5,
                        Minimum = .2,
                        Maximum = .9,
                        Incomplete = true
                    },
                    new DeploymentDailyTelemetry
                    {
                        UserId = other.Id,
                        ProjectId = outsider.Id,
                        DeploymentId = unknown.Id,
                        DayUtc = day,
                        Container = "private",
                        Metric = "automate_cpu_usage_cores",
                        Unit = "cores",
                        SampleCount = 100,
                        Sum = 100
                    });
                db.CloudDeploymentRuns.Add(new CloudDeploymentRun
                {
                    UserId = owner.Id,
                    ProjectId = apps[2].Id,
                    IdempotencyKey = "fixture",
                    Phase = CloudRunPhase.Queued
                });
                await db.SaveChangesAsync();
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