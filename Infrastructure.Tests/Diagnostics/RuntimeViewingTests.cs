using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Verifies live viewing is scoped, expiring, bounded, and saves replay independently of background consent.</summary>
public sealed class RuntimeViewingTests
{
    /// <summary>Disconnect, reconnect and lease expiry release collection interest without affecting other viewers.</summary>
    [Fact]
    public void Viewer_leases_are_deployment_scoped_and_expire()
    {
        var clock = new TestClock();
        var viewers = new DeploymentRuntimeViewers(clock);
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        viewers.Renew("one", project, deployment);
        viewers.Renew("two", project, deployment);
        viewers.HasViewers(project, Guid.NewGuid()).Should().BeFalse();
        viewers.HasViewers(Guid.NewGuid(), deployment).Should().BeFalse();
        viewers.Remove("one");
        viewers.HasViewers(project, deployment).Should().BeTrue();
        clock.Now += TimeSpan.FromSeconds(40);
        viewers.Renew("two", project, deployment);
        clock.Now += TimeSpan.FromSeconds(40);
        viewers.HasViewers(project, deployment).Should().BeTrue();
        clock.Now += TimeSpan.FromSeconds(6);
        viewers.HasViewers(project, deployment).Should().BeFalse();
    }

    /// <summary>Abandoned connections cannot grow the registry without bound.</summary>
    [Fact]
    public void Viewer_capacity_is_bounded_and_recovers_after_expiry()
    {
        var clock = new TestClock();
        var viewers = new DeploymentRuntimeViewers(clock);
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        for (var i = 0; i < 4096; i++) viewers.Renew(i.ToString(), project, deployment);
        viewers.Invoking(v => v.Renew("extra", project, deployment)).Should().Throw<InvalidOperationException>();
        viewers.Renew("0", project, deployment);
        clock.Now += TimeSpan.FromSeconds(46);
        viewers.Renew("extra", project, deployment);
        viewers.HasViewers(project, deployment).Should().BeTrue();
    }

    /// <summary>Viewed runtime output is saved; unattended collection requires explicit background consent.</summary>
    [Fact]
    public async Task Viewed_runtime_is_persisted_for_replay_without_enabling_background_collection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AutoMateDbContext(new DbContextOptionsBuilder<AutoMateDbContext>()
            .UseSqlite(connection).Options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var app = new Domain.Entities.Application
        {
            Name = "sample", SourcePathOrUrl = "C:/sample", SourceType = SourceType.Local,
            User = new LocalUser { Username = "test", Email = "test@example.invalid" }
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        var viewers = new DeploymentRuntimeViewers(TimeProvider.System);
        var store = new DeploymentTelemetryStore(db, new DeploymentDiagnosticStore(db), new UnusedQuery(),
            Options.Create(new TelemetryStorageOptions()), new DiagnosticRedactor(),
            NullLogger<DeploymentTelemetryStore>.Instance, viewers);
        var deployment = Guid.NewGuid();
        db.Deployments.Add(new Deployment
        {
            Id = deployment,
            CsProject = new CsProject { Application = app, Name = "Web", Path = "Web.csproj" }
        });
        await db.SaveChangesAsync();
        var log = new DeploymentDiagnosticEvent(app.Id, deployment, DeploymentDiagnosticSource.DockerContainer,
            DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            "sample", new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"));
        (await store.PersistAsync(log, "web")).Should().Be(0);
        viewers.Renew("owner", app.Id, deployment);
        await store.PersistAsync(log, "web");
        // SQLite has no PostgreSQL ordering sequence; advance its test row before inserting the next event.
        await db.Database.ExecuteSqlRawAsync("UPDATE DeploymentDiagnosticRecords SET OrderId = 1");
        (await store.PersistAsync(log with { DeploymentId = Guid.NewGuid() }, "web")).Should().Be(0);
        var metric = log with
        {
            Kind = DeploymentDiagnosticKind.Metric,
            TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, "web"),
            Metrics = [new DeploymentMetricSample("automate_cpu_usage_cores", 0.25, "cores")]
        };
        await store.PersistAsync(metric, null);
        await db.Database.ExecuteSqlRawAsync("UPDATE DeploymentDiagnosticRecords SET OrderId = 2 WHERE OrderId = 0");
        (await db.DeploymentDiagnosticRecords.CountAsync()).Should().Be(2);
        viewers.Remove("owner");
        (await store.PersistAsync(log, "web")).Should().Be(0);
        app.RuntimeDiagnosticsEnabled = true;
        await db.SaveChangesAsync();
        await store.PersistAsync(log, "web");
        (await db.DeploymentDiagnosticRecords.CountAsync()).Should().Be(3);
    }

    /// <summary>Deterministic lease clock.</summary>
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    /// <summary>Fails if unsaved live viewing unexpectedly queries an external backend.</summary>
    private sealed class UnusedQuery : IDeploymentLogQuery
    {
        public Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenantId, Guid projectId, Guid deploymentId,
            long cursor, bool backwards, int limit, CancellationToken token, DateTimeOffset? start = null)
        {
            throw new InvalidOperationException();
        }

        public Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            throw new InvalidOperationException();
        }
    }
}