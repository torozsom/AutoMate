using System.Diagnostics;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Real PostgreSQL/Loki/Mimir acceptance tests against the isolated Compose override.</summary>
public sealed class TelemetryIntegrationTests(ITestOutputHelper output)
{
    /// <summary>Applies the actual schema and verifies replay before and after query visibility.</summary>
    [TelemetryIntegrationFact]
    public async Task Real_stores_replay_redacted_logs_and_numeric_metrics_after_buffer_removal()
    {
        using var services = Services();
        var seeded = await SeedAsync(services);
        await using (var scope = services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>();
            await store.PersistAsync(Event(seeded, "password=secret"), "build");
            await store.PersistAsync(Event(seeded, "second"), "build");
            await store.PersistAsync(Event(seeded, "metrics") with
            {
                Kind = DeploymentDiagnosticKind.Metric,
                Source = DeploymentDiagnosticSource.DockerContainer,
                TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, "web"),
                Metrics =
                [
                    new DeploymentMetricSample("automate_cpu_usage_cores", 2.5, "cores"),
                    new DeploymentMetricSample("automate_memory_used_bytes", 1024, "bytes")
                ]
            }, null);
            var initial = await store.ReadRecentAsync(seeded.Project, seeded.Deployment, 500);
            initial.Events.Select(e => e.Message).Should().Equal("password=[REDACTED]", "second");
        }

        var worker = new TelemetryDeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(TelemetryStorageTests.Specialized()), NullLogger<TelemetryDeliveryWorker>.Instance);
        for (var i = 0; i < 20; i++)
        {
            await worker.DeliverOnceAsync(CancellationToken.None);
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            if (!await db.DeploymentDiagnosticRecords.AnyAsync(r => r.ProjectId == seeded.Project)) break;
            await Task.Delay(2000);
        }

        await using var finalScope = services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        (await finalDb.DeploymentDiagnosticRecords.CountAsync(r => r.ProjectId == seeded.Project)).Should().Be(0,
            "delivery must confirm both Loki events and exact raw metric samples");
        var history = finalScope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>();
        var page = await history.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment, 0, true, 1);
        page.Events.Should().ContainSingle().Which.Message.Should().Be("second");
        page.EarlierOmitted.Should().BeTrue();
        var older = await history.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment, page.Events[0].OrderId,
            true, 10);
        older.Events.Should().ContainSingle().Which.Message.Should().Be("password=[REDACTED]");
        var points = await history.ReadMetricsAsync(seeded.Owner, seeded.Project, seeded.Deployment,
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow, 10);
        points.Points.Should().Contain(p => p.Name == "automate_cpu_usage_cores" && p.Average == 2.5);
        await history.Invoking(h => h.ReadLogsAsync(Guid.NewGuid(), seeded.Project, seeded.Deployment, 0, true, 10))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    /// <summary>Concurrent processes share atomic byte admission and stable cursors.</summary>
    [TelemetryIntegrationFact]
    public async Task Concurrent_ingestion_is_bounded_and_runtime_opt_out_is_enforced()
    {
        var options = TelemetryStorageTests.Specialized();
        options.TenantBufferBytes = 16000;
        using var services = Services(options);
        var seeded = await SeedAsync(services);
        var durations = new List<double>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = 12 },
            async (i, token) =>
            {
                var started = Stopwatch.GetTimestamp();
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>()
                    .PersistAsync(Event(seeded, $"line {i}"), "build", token);
                lock (durations)
                {
                    durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            });
        await using var readScope = services.CreateAsyncScope();
        var db = readScope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var rows = await db.DeploymentDiagnosticRecords.Where(r => r.ProjectId == seeded.Project).ToListAsync();
        rows.Sum(r => r.DeliveryBytes).Should().BeLessThanOrEqualTo(16000);
        rows.Select(r => r.OrderId).Should().OnlyHaveUniqueItems();
        var state = await db.TelemetryTenantStates.SingleAsync(s => s.TenantId == seeded.Owner);
        state.DroppedEvents.Should().BeGreaterThan(0);
        var history = readScope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>();
        (await history.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment, 0, true, 500))
            .Availability.Should().Contain("omitted");
        await history.SetRuntimeCollectionAsync(seeded.Owner, seeded.Project, false);
        (await readScope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>().PersistAsync(
            Event(seeded, "runtime") with
            {
                Source = DeploymentDiagnosticSource.DockerContainer
            }, "web")).Should().Be(0);
        var ordered = durations.Order().ToArray();
        output.WriteLine(
            $"Concurrent pilot ingestion: 100 events, 12 callers, p95 {ordered[94]:F1} ms; buffer {rows.Sum(r => r.DeliveryBytes)} bytes.");
    }

    /// <summary>Reclaims a dead worker lease and never drops a partially visible prefix.</summary>
    [TelemetryIntegrationFact]
    public async Task Lease_recovery_waits_for_complete_visibility_and_records_buffer_expiry()
    {
        var sinks = new DelayedSinks();
        using var services = Services(sinks: sinks);
        var seeded = await SeedAsync(services);
        await using (var scope = services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>();
            await store.PersistAsync(Event(seeded, "first"), "build");
            await store.PersistAsync(Event(seeded, "second"), "build");
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            await db.TelemetryTenantStates.Where(s => s.TenantId == seeded.Owner).ExecuteUpdateAsync(u =>
                u.SetProperty(s => s.LeaseId, Guid.NewGuid())
                    .SetProperty(s => s.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        var worker = new TelemetryDeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(TelemetryStorageTests.Specialized()),
            NullLogger<TelemetryDeliveryWorker>.Instance);
        await worker.DeliverOnceAsync(CancellationToken.None);
        await using var readScope = services.CreateAsyncScope();
        var dbRead = readScope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        (await dbRead.DeploymentDiagnosticRecords.CountAsync(r => r.ProjectId == seeded.Project)).Should().Be(2);
        await dbRead.TelemetryTenantStates.Where(s => s.TenantId == seeded.Owner)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DueAt, DateTimeOffset.UtcNow));
        sinks.Visible = true;
        await worker.DeliverOnceAsync(CancellationToken.None);
        (await dbRead.DeploymentDiagnosticRecords.CountAsync(r => r.ProjectId == seeded.Project)).Should().Be(0);
        sinks.Writes.Should().Be(1, "accepted batches awaiting visibility should not be resent");
        await readScope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>()
            .PersistAsync(Event(seeded, "expired buffer"), "build");
        await dbRead.DeploymentDiagnosticRecords.Where(r => r.ProjectId == seeded.Project).ExecuteUpdateAsync(u =>
            u.SetProperty(r => r.BufferExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
        await dbRead.TelemetryTenantStates.Where(s => s.TenantId == seeded.Owner)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DueAt, DateTimeOffset.UtcNow));
        await worker.DeliverOnceAsync(CancellationToken.None);
        (await dbRead.TelemetryTenantStates.AsNoTracking().SingleAsync(s => s.TenantId == seeded.Owner)).DroppedEvents
            .Should().Be(1);
    }

    /// <summary>Legacy rows remain pageable during migration, and PostgreSQL remains a working fallback.</summary>
    [TelemetryIntegrationFact]
    public async Task Legacy_history_is_pageable_and_expired_rows_stay_hidden_in_both_modes()
    {
        using var services = Services(sinks: new DelayedSinks());
        var seeded = await SeedAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        db.DeploymentDiagnosticRecords.AddRange(
            new DeploymentDiagnosticRecord
            {
                ProjectId = seeded.Project,
                TimestampUtc = DateTimeOffset.UtcNow,
                Source = "DockerCompose",
                Kind = "Log",
                Severity = "Information",
                Message = "legacy earlier",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            },
            new DeploymentDiagnosticRecord
            {
                ProjectId = seeded.Project,
                DeploymentId = seeded.Deployment,
                TimestampUtc = DateTimeOffset.UtcNow,
                Source = "DockerCompose",
                Kind = "Log",
                Severity = "Information",
                Message = "recent",
                TerminalChannel = "build",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            },
            new DeploymentDiagnosticRecord
            {
                ProjectId = seeded.Project,
                DeploymentId = seeded.Deployment,
                TimestampUtc = DateTimeOffset.UtcNow,
                Source = "DockerCompose",
                Kind = "Log",
                Severity = "Information",
                Message = "expired",
                TerminalChannel = "build",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            });
        await db.SaveChangesAsync();
        var specialized = scope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>();
        var recent = await specialized.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment, 0, true, 1);
        recent.Events.Should().ContainSingle().Which.Message.Should().Be("recent");
        var earlier = await specialized.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment,
            recent.Events[0].OrderId, true, 1);
        earlier.Events.Should().ContainSingle().Which.Message.Should().Be("legacy earlier");
        earlier.Events[0].TerminalChannel.Should().Be("build");

        using var fallback = Services(new TelemetryStorageOptions());
        await using var fallbackScope = fallback.CreateAsyncScope();
        var history = fallbackScope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>();
        (await history.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment, 0, true, 10))
            .Events.Select(e => e.Message).Should().Equal("legacy earlier", "recent");
        await fallbackScope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>().DeleteExpiredAsync(10000);
        (await db.DeploymentDiagnosticRecords.CountAsync(r => r.ProjectId == seeded.Project)).Should().Be(2);
        await history.SetRuntimeCollectionAsync(seeded.Owner, seeded.Project, false);
        (await fallbackScope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>().PersistAsync(
            Event(seeded, "runtime") with
            {
                Source = DeploymentDiagnosticSource.DockerContainer
            }, "web")).Should().Be(0);
    }

    /// <summary>External ingestion and queries require consent, while build diagnostics remain available locally.</summary>
    [TelemetryIntegrationFact]
    public async Task Managed_consent_controls_external_storage_without_losing_build_diagnostics()
    {
        var settings = TelemetryStorageTests.Specialized();
        settings.ManagedService = true;
        settings.ManagedDataProcessingApproved = true;
        settings.ProcessingRegion = "EU";
        var sinks = new DelayedSinks();
        using var services = Services(settings, sinks);
        var seeded = await SeedAsync(services);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>();
        var history = scope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>();
        await store.PersistAsync(Event(seeded, "before consent"), "build");
        (await history.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment, 0, true, 10))
            .Events.Should().ContainSingle().Which.Message.Should().Be("before consent");
        await history.ReadMetricsAsync(seeded.Owner, seeded.Project, seeded.Deployment,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 10);
        sinks.Reads.Should().Be(0);
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        (await db.DeploymentDiagnosticRecords.SingleAsync(r => r.ProjectId == seeded.Project)).DeliveryJson.Should()
            .BeNull();
        await history.SetManagedConsentAsync(seeded.Owner, seeded.Project, true);
        await store.PersistAsync(Event(seeded, "after consent"), "build");
        (await db.DeploymentDiagnosticRecords.CountAsync(r => r.ProjectId == seeded.Project && r.DeliveryJson != null))
            .Should().Be(1);
        await history.SetManagedConsentAsync(seeded.Owner, seeded.Project, false);
        var worker = new TelemetryDeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            NullLogger<TelemetryDeliveryWorker>.Instance);
        await worker.DeliverOnceAsync(CancellationToken.None);
        sinks.Writes.Should().Be(0);
        (await db.DeploymentDiagnosticRecords.CountAsync(r => r.ProjectId == seeded.Project && r.DeliveryJson != null))
            .Should().Be(0);
        await store.PersistAsync(Event(seeded, "after revocation"), "build");
        (await db.DeploymentDiagnosticRecords.CountAsync(r => r.ProjectId == seeded.Project && r.DeliveryJson == null))
            .Should().Be(2);
    }

    /// <summary>Builds independent scopes sharing an isolated real database.</summary>
    private static ServiceProvider Services(TelemetryStorageOptions? settings = null, DelayedSinks? sinks = null)
    {
        var options = Options.Create(settings ?? TelemetryStorageTests.Specialized());
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IDeploymentRuntimeViewers, DeploymentRuntimeViewers>()
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .AddDbContext<AutoMateDbContext>(b =>
                b.UseNpgsql(Environment.GetEnvironmentVariable("AUTOMATE_TELEMETRY_TEST_DB"))
                    .UseSnakeCaseNamingConvention())
            .AddSingleton(options)
            .AddSingleton<IDiagnosticRedactor, DiagnosticRedactor>()
            .AddSingleton<TelemetryHttpTransport>().AddScoped<DeploymentDiagnosticStore>()
            .AddScoped<DeploymentTelemetryStore>()
            .AddScoped<IDeploymentHistoryService, DeploymentHistoryService>()
            .AddScoped<LokiDeploymentLogs>().AddScoped<MimirDeploymentMetrics>()
            .AddScoped<IDeploymentLogWriter>(p => p.GetRequiredService<LokiDeploymentLogs>())
            .AddScoped<IDeploymentLogQuery>(p => p.GetRequiredService<LokiDeploymentLogs>())
            .AddScoped<IDeploymentMetricWriter>(p => p.GetRequiredService<MimirDeploymentMetrics>())
            .AddScoped<IDeploymentMetricQuery>(p => p.GetRequiredService<MimirDeploymentMetrics>());
        services.AddHttpClient("DeploymentTelemetry", client => client.Timeout = TimeSpan.FromSeconds(10));
        if (sinks is not null)
            services.AddSingleton<IDeploymentLogWriter>(sinks).AddSingleton<IDeploymentLogQuery>(sinks)
                .AddSingleton<IDeploymentMetricWriter>(sinks).AddSingleton<IDeploymentMetricQuery>(sinks);
        return services.BuildServiceProvider();
    }

    /// <summary>Seeds unique project identities and uses real migrations.</summary>
    private static async Task<(Guid Owner, Guid Project, Guid Deployment)> SeedAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        await db.Database.MigrateAsync();
        var owner = new LocalUser { Username = "test", Email = $"{Guid.NewGuid():N}@example.invalid" };
        var project = new Domain.Entities.Application
        {
            User = owner,
            Name = "Sample",
            SourceType = SourceType.Local,
            SourcePathOrUrl = "C:/test",
            RuntimeDiagnosticsEnabled = true
        };
        var deployment = new Deployment
            { CsProject = new CsProject { Application = project, Name = "Web", Path = "Web.csproj" } };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();
        return (owner.Id, project.Id, deployment.Id);
    }

    /// <summary>Constructs a provider observation without credentials.</summary>
    private static DeploymentDiagnosticEvent Event((Guid Owner, Guid Project, Guid Deployment) seed, string message)
    {
        return new DeploymentDiagnosticEvent(seed.Project, seed.Deployment, DeploymentDiagnosticSource.DockerCompose,
            DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, message,
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build));
    }

    /// <summary>Simulates successful ingestion whose query visibility is delayed.</summary>
    private sealed class DelayedSinks : IDeploymentLogWriter, IDeploymentLogQuery, IDeploymentMetricWriter,
        IDeploymentMetricQuery
    {
        /// <summary>Controls complete-batch visibility.</summary>
        public bool Visible { get; set; }

        /// <summary>Counts accepted log batches.</summary>
        public int Writes { get; private set; }

        /// <summary>Counts external query attempts for consent verification.</summary>
        public int Reads { get; private set; }

        /// <inheritdoc />
        public Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            return Task.FromResult(Visible || events.Count == 0);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            long cursor, bool backwards, int limit, CancellationToken token, DateTimeOffset? start = null)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<DeploymentLogEnvelope>>([]);
        }

        /// <inheritdoc />
        Task IDeploymentLogWriter.WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            Writes++;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DeploymentMetricPoint>> ReadAsync(Guid tenant, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken token)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<DeploymentMetricPoint>>([]);
        }

        /// <inheritdoc />
        Task IDeploymentMetricWriter.WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken token)
        {
            return Task.CompletedTask;
        }
    }
}

/// <summary>Requires explicit configuration of an isolated integration database.</summary>
public sealed class TelemetryIntegrationFactAttribute : FactAttribute
{
    /// <summary>Default test runs never contact a user's application database.</summary>
    public TelemetryIntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTOMATE_TELEMETRY_TEST_DB")))
            Skip = "Set AUTOMATE_TELEMETRY_TEST_DB to the isolated telemetry Compose database.";
    }
}