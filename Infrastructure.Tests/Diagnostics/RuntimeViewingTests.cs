using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Runtime output is saved automatically even with legacy flags disabled and no viewers.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_is_persisted_automatically_with_legacy_flags_disabled(bool managed)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AutoMateDbContext(new DbContextOptionsBuilder<AutoMateDbContext>()
            .UseSqlite(connection).Options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var app = new Domain.Entities.Application
        {
            Name = "sample",
            SourcePathOrUrl = "C:/sample",
            SourceType = SourceType.Local,
            User = new LocalUser { Username = "test", Email = "test@example.invalid" }
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        var viewers = new DeploymentRuntimeViewers(TimeProvider.System);
        using var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        using var policies = new TelemetryProjectPolicyCache(services.GetRequiredService<IServiceScopeFactory>());
        var gateway = new RecordingGateway();
        var settings = Options.Create(new TelemetryStorageOptions
        {
            Backend = "LokiMimir", DeliveryMode = "DiskGateway", ManagedService = managed,
            ManagedDataProcessingApproved = true
        });
        var store = new DeploymentTelemetryStore(db, new DeploymentDiagnosticStore(db, new DiagnosticRedactor()),
            new UnusedQuery(),
            settings, new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance, viewers, gateway,
            policies);
        var deployment = Guid.NewGuid();
        db.Deployments.Add(new Deployment
        {
            Id = deployment,
            CsProject = new CsProject { Application = app, Name = "Web", Path = "Web.csproj" }
        });
        await db.SaveChangesAsync();
        await db.Applications.ExecuteUpdateAsync(u => u.SetProperty(p => p.RuntimeDiagnosticsEnabled, false)
            .SetProperty(p => p.ManagedTelemetryConsent, false));
        var log = new DeploymentDiagnosticEvent(app.Id, deployment, DeploymentDiagnosticSource.DockerContainer,
            DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            "sample", new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"));
        (await store.PersistAsync(log, "web")).Should().BeGreaterThan(0);
        viewers.Renew("owner", app.Id, deployment);
        await store.PersistAsync(log, "web");
        (await store.PersistAsync(log with { DeploymentId = Guid.NewGuid() }, "web")).Should().Be(0);
        var metric = log with
        {
            Kind = DeploymentDiagnosticKind.Metric,
            TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, "web"),
            Metrics = [new DeploymentMetricSample("automate_cpu_usage_cores", 0.25, "cores")]
        };
        await store.PersistAsync(metric, null);
        gateway.Count.Should().Be(3);
        (await db.DeploymentDiagnosticRecords.CountAsync()).Should().Be(0);
        viewers.Remove("owner");
        (await store.PersistAsync(log, "web")).Should().BeGreaterThan(0);
        app.RuntimeDiagnosticsEnabled = true;
        await db.SaveChangesAsync();
        using var refreshedPolicies =
            new TelemetryProjectPolicyCache(services.GetRequiredService<IServiceScopeFactory>());
        var backgroundStore = new DeploymentTelemetryStore(db,
            new DeploymentDiagnosticStore(db, new DiagnosticRedactor()), new UnusedQuery(),
            settings, new DiagnosticRedactor(), NullLogger<DeploymentTelemetryStore>.Instance, viewers, gateway,
            refreshedPolicies);
        await backgroundStore.PersistAsync(log, "web");
        gateway.Count.Should().Be(5);
        (await db.DeploymentDiagnosticRecords.CountAsync()).Should().Be(0);
        var history = new DeploymentHistoryService(db, store, new UnusedMetrics(), settings);
        await history.SetRuntimeCollectionAsync(app.UserId, app.Id, false);
        await history.SetManagedConsentAsync(app.UserId, app.Id, false);
        var enabled = await history.GetPreferencesAsync(app.UserId, app.Id);
        enabled.RuntimeEnabled.Should().BeTrue();
        enabled.ManagedConsent.Should().BeTrue();
        var persisted = await db.Applications.AsNoTracking().SingleAsync();
        persisted.RuntimeDiagnosticsEnabled.Should().BeTrue();
        persisted.ManagedTelemetryConsent.Should().BeTrue();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            history.SetRuntimeCollectionAsync(Guid.NewGuid(), app.Id, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            history.SetManagedConsentAsync(Guid.NewGuid(), app.Id, false));
    }

    /// <summary>Preference changes do not query external metrics.</summary>
    private sealed class UnusedMetrics : IDeploymentMetricQuery
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<DeploymentMetricPoint>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, int maximum, CancellationToken token)
        {
            throw new InvalidOperationException();
        }

        /// <inheritdoc />
        public Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            throw new InvalidOperationException();
        }
    }

    /// <summary>Tracks confirmed disk-gateway submissions; no PostgreSQL payload or ordering sequence is used.</summary>
    private sealed class RecordingGateway : ITelemetryGateway
    {
        /// <summary>Number of accepted observations.</summary>
        public int Count { get; private set; }

        /// <inheritdoc />
        public Task<DeploymentLogEnvelope> AcceptAsync(DeploymentDiagnosticEvent e, string? channel,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new DeploymentLogEnvelope(e.EventId!.Value, Guid.NewGuid(), ++Count,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), e, channel));
        }

        /// <inheritdoc />
        public Task<TelemetryPendingHistory> ReadPendingAsync(Guid tenant, Guid project, Guid deployment,
            CancellationToken token)
        {
            return Task.FromResult(new TelemetryPendingHistory([], 0));
        }
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