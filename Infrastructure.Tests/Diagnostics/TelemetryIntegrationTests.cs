using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
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
    /// <summary>Real LogQL applies typed source/container filters before its candidate limit.</summary>
    [TelemetryIntegrationFact]
    public async Task Assessment_filters_selected_sources_before_backend_limits()
    {
        using var services = Services();
        var seed = await SeedAsync(services);
        await using var scope = services.CreateAsyncScope();
        var logs = scope.ServiceProvider.GetRequiredService<IDeploymentLogWriter>();
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(1, 30).Select(i => new DeploymentLogEnvelope(Guid.NewGuid(), seed.Owner, i, now,
            now.AddDays(30), Event(seed, "excluded database output") with
            {
                Source = DeploymentDiagnosticSource.DockerContainer,
                SourceIdentity = new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Database,
                    DeploymentDiagnosticStream.StandardOutput),
                TimestampUtc = now.AddSeconds(-i),
                TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "db")
            }, "db")).ToList();
        rows.Add(new DeploymentLogEnvelope(Guid.NewGuid(), seed.Owner, 31, now, now.AddDays(30),
            Event(seed, "selected web output") with
            {
                Source = DeploymentDiagnosticSource.AzureContainerApps,
                SourceIdentity = new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Container,
                    DeploymentDiagnosticStream.StandardOutput),
                TimestampUtc = now.AddSeconds(-40),
                TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web")
            }, "web"));
        await logs.WriteAsync(rows, default);
        var query = new ArchiveAssessmentQuery(seed.Owner, seed.Project, seed.Deployment,
            new AssessmentSelection(Sources: AssessmentSources.Web, IncludeMetrics: false, LogContainers: ["web"]),
            new AssessmentWindow(now.AddMinutes(-5), now.AddMinutes(1), false), 1);
        var reader = scope.ServiceProvider.GetRequiredService<IDeploymentLogQuery>();
        IReadOnlyList<DeploymentLogEnvelope> selected = [];
        for (var attempt = 0; attempt < 30 && selected.Count == 0; attempt++)
        {
            selected = await reader.ReadAssessmentAsync(query, default);
            if (selected.Count == 0) await Task.Delay(1000);
        }

        Assert.Single(selected);
        Assert.Equal("selected web output", selected[0].Event.Message);
        Assert.Empty(await reader.ReadAssessmentAsync(
            query with { Selection = new AssessmentSelection(Sources: AssessmentSources.Azure) }, default));
        Assert.Empty(await reader.ReadAssessmentAsync(query with { Tenant = Guid.NewGuid() }, default));
        var metricRows = new[] { "web", "db" }.Select((container, i) => new DeploymentLogEnvelope(Guid.NewGuid(),
            seed.Owner,
            100 + i, now, now.AddDays(30), Event(seed, "metric") with
            {
                Kind = DeploymentDiagnosticKind.Metric, Source = DeploymentDiagnosticSource.DockerContainer,
                TimestampUtc = now.AddMinutes(-2),
                TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, container),
                Metrics = [new DeploymentMetricSample("automate_memory_used_bytes", 4096 + i, "bytes")]
            }, null)).ToArray();
        await scope.ServiceProvider.GetRequiredService<IDeploymentMetricWriter>().WriteAsync(metricRows, default);
        var metricQuery = query with
        {
            Selection = new AssessmentSelection(Sources: AssessmentSources.None, MetricContainers: ["web"])
        };
        var metricReader = scope.ServiceProvider.GetRequiredService<IDeploymentMetricQuery>();
        IReadOnlyList<DeploymentMetricPoint> points = [];
        for (var attempt = 0; attempt < 30 && points.Count == 0; attempt++)
        {
            points = await metricReader.ReadAssessmentAsync(metricQuery, default);
            if (points.Count == 0) await Task.Delay(1000);
        }

        Assert.NotEmpty(points);
        Assert.All(points, point => Assert.Equal("web", point.Container));
        Assert.Empty(await metricReader.ReadAssessmentAsync(
            metricQuery with { Selection = new AssessmentSelection(IncludeMetrics: false) }, default));
    }

    [TelemetryIntegrationFact]
    public async Task Disk_receipts_recover_and_reach_real_stores_without_database_payloads()
    {
        var settings = TelemetryStorageTests.Specialized();
        settings.DeliveryMode = "DiskGateway";
        using var services = Services(settings);
        var seeded = await SeedAsync(services);
        var directory = Path.Combine(Path.GetTempPath(), "automate-integration-spool-" + Guid.NewGuid().ToString("N"));

        DiskTelemetrySpool Create()
        {
            return new DiskTelemetrySpool(Options.Create(new DiskSpoolOptions { Directory = directory }),
                Options.Create(settings), NullLogger<DiskTelemetrySpool>.Instance, new DiagnosticRedactor());
        }

        try
        {
            using (var spool = Create())
            {
                await spool.StartAsync(default);
                await spool.AppendAsync(seeded.Owner,
                    Event(seeded, "durable disk log") with { EventId = Guid.NewGuid() }, "build", default);
                await spool.AppendAsync(seeded.Owner, Event(seeded, "metrics") with
                {
                    EventId = Guid.NewGuid(),
                    Kind = DeploymentDiagnosticKind.Metric,
                    TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, "web"),
                    Metrics = [new DeploymentMetricSample("automate_cpu_usage_cores", 1.25, "cores")]
                }, null, default);
                await spool.StopAsync(default);
            }

            using var recovered = Create();
            await recovered.StartAsync(default);
            var worker = new DiskTelemetryDeliveryWorker(recovered, services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(settings), NullLogger<DiskTelemetryDeliveryWorker>.Instance);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                foreach (var path in await recovered.SegmentPathsAsync(default))
                    await worker.DeliverAsync(path, default);
                if (Directory.GetFiles(directory, "*.segment").Length == 0) break;
                await Task.Delay(1000);
            }

            Assert.Empty(Directory.GetFiles(directory, "*.segment"));
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            Assert.False(await db.DeploymentDiagnosticRecords.AnyAsync(r => r.ProjectId == seeded.Project));
            var logs = await scope.ServiceProvider.GetRequiredService<IDeploymentLogQuery>()
                .ReadAsync(seeded.Owner, seeded.Project, seeded.Deployment, 0, false, 10, default);
            Assert.Single(logs, e => e.Event.Message == "durable disk log");
            var daily = await scope.ServiceProvider.GetRequiredService<MimirDeploymentMetrics>()
                .ReadDailyAsync(seeded.Owner, seeded.Project, seeded.Deployment, DateTimeOffset.UtcNow.AddMinutes(-10),
                    DateTimeOffset.UtcNow, default);
            Assert.Contains(daily,
                row => row.Name == "automate_cpu_usage_cores" && row.SampleCount == 1 && row.Sum == 1.25);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IDeploymentLogQuery>()
                .ReadAsync(Guid.NewGuid(), seeded.Project, seeded.Deployment, 0, false, 10, default));
            await recovered.StopAsync(default);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>Applies the actual schema and verifies replay before and after query visibility.</summary>
    [TelemetryIntegrationFact]
    public async Task Real_stores_replay_redacted_logs_and_numeric_metrics_after_buffer_removal()
    {
        using var services = Services();
        var seeded = await SeedAsync(services);
        await using (var scope = services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<DeploymentTelemetryStore>();
            await SeedLegacyBufferAsync(services, Event(seeded, "password=secret"), "build");
            await SeedLegacyBufferAsync(services, Event(seeded, "second"), "build");
            await SeedLegacyBufferAsync(services, Event(seeded, "metrics") with
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

    /// <summary>Legacy modes reject all new concurrent ingestion while leaving the historical database untouched.</summary>
    [TelemetryIntegrationFact]
    public async Task Concurrent_legacy_ingestion_is_rejected_without_database_payloads()
    {
        using var services = Services();
        var seeded = await SeedAsync(services);
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = 12 },
            async (i, token) =>
            {
                await using var scope = services.CreateAsyncScope();
                await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                    .GetRequiredService<DeploymentTelemetryStore>()
                    .PersistAsync(Event(seeded, $"line {i}"), "build", token));
            });
        await using var readScope = services.CreateAsyncScope();
        Assert.False(await readScope.ServiceProvider.GetRequiredService<AutoMateDbContext>()
            .DeploymentDiagnosticRecords.AnyAsync(row => row.ProjectId == seeded.Project));
        output.WriteLine("Concurrent legacy ingestion: 100 direct calls rejected without new PostgreSQL payloads.");
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
            await SeedLegacyBufferAsync(services, Event(seeded, "first"), "build");
            await SeedLegacyBufferAsync(services, Event(seeded, "second"), "build");
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
        await SeedLegacyBufferAsync(services, Event(seeded, "expired buffer"), "build");
        await dbRead.DeploymentDiagnosticRecords.Where(r => r.ProjectId == seeded.Project).ExecuteUpdateAsync(u =>
            u.SetProperty(r => r.BufferExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
        await dbRead.TelemetryTenantStates.Where(s => s.TenantId == seeded.Owner)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DueAt, DateTimeOffset.UtcNow));
        await worker.DeliverOnceAsync(CancellationToken.None);
        (await dbRead.TelemetryTenantStates.AsNoTracking().SingleAsync(s => s.TenantId == seeded.Owner)).DroppedEvents
            .Should().Be(1);
    }

    /// <summary>Legacy rows remain pageable and expiring in both query modes; new PostgreSQL ingestion is denied.</summary>
    [TelemetryIntegrationFact]
    public async Task Legacy_history_is_pageable_and_expired_rows_stay_hidden_in_both_modes()
    {
        using var services = Services(sinks: new DelayedSinks());
        var seeded = await SeedAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        DeploymentDiagnosticRecord[] chronologicalRows =
        [
            new()
            {
                ProjectId = seeded.Project,
                TimestampUtc = DateTimeOffset.UtcNow,
                Source = "DockerCompose",
                Kind = "Log",
                Severity = "Information",
                Message = "legacy earlier",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            },
            new()
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
            new()
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
            }
        ];
        // Separate ingestion writes preserve cursor order; EF may reorder entities within a bulk INSERT batch.
        foreach (var row in chronologicalRows)
        {
            db.DeploymentDiagnosticRecords.Add(row);
            await db.SaveChangesAsync();
        }

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
        await Assert.ThrowsAsync<InvalidOperationException>(() => fallbackScope.ServiceProvider
            .GetRequiredService<DeploymentTelemetryStore>()
            .PersistAsync(Event(seeded, "runtime") with { Source = DeploymentDiagnosticSource.DockerContainer },
                "web"));
    }

    /// <summary>Consent gates external historical queries/draining but never enables new PostgreSQL fallback writes.</summary>
    [TelemetryIntegrationFact]
    public async Task Managed_consent_controls_legacy_delivery_without_new_database_ingestion()
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
        await SeedLegacyBufferAsync(services, Event(seeded, "legacy before consent"), "build", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistAsync(Event(seeded, "new before consent"), "build"));
        Assert.Single(
            (await history.ReadLogsAsync(seeded.Owner, seeded.Project, seeded.Deployment, 0, true, 10)).Events);
        await history.ReadMetricsAsync(seeded.Owner, seeded.Project, seeded.Deployment,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 10);
        Assert.Equal(0, sinks.Reads);
        await history.SetManagedConsentAsync(seeded.Owner, seeded.Project, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistAsync(Event(seeded, "new after consent"), "build"));
        await SeedLegacyBufferAsync(services, Event(seeded, "legacy pending"), "build");
        await history.SetManagedConsentAsync(seeded.Owner, seeded.Project, false);
        var worker = new TelemetryDeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings), NullLogger<TelemetryDeliveryWorker>.Instance);
        await worker.DeliverOnceAsync(default);
        Assert.Equal(0, sinks.Writes);
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        Assert.False(await db.DeploymentDiagnosticRecords.AnyAsync(row =>
            row.ProjectId == seeded.Project && row.DeliveryJson != null));
        Assert.Single(await db.DeploymentDiagnosticRecords.Where(row => row.ProjectId == seeded.Project).ToListAsync());
    }

    /// <summary>Seeds historical migration fixtures only; production ingestion never creates these records or outboxes.</summary>
    private static async Task SeedLegacyBufferAsync(ServiceProvider services, DeploymentDiagnosticEvent diagnostic,
        string? channel, bool buffered = true)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var tenant = await db.Applications.Where(item => item.Id == diagnostic.ProjectId).Select(item => item.UserId)
            .SingleAsync();
        var now = DateTimeOffset.UtcNow;
        diagnostic = new DiagnosticRedactor().Redact(diagnostic with { EventId = diagnostic.EventId ?? Guid.NewGuid() })
            .Event;
        db.DeploymentDiagnosticRecords.Add(new DeploymentDiagnosticRecord
        {
            TenantId = buffered ? tenant : null,
            ProjectId = diagnostic.ProjectId,
            DeploymentId = diagnostic.DeploymentId,
            TimestampUtc = diagnostic.TimestampUtc,
            Source = diagnostic.Source.ToString(),
            Kind = diagnostic.Kind.ToString(),
            Severity = diagnostic.Severity.ToString(),
            Message = diagnostic.Message,
            TerminalChannel = channel,
            ExpiresAt = now.AddDays(30),
            StoredAt = buffered ? now : null,
            BufferExpiresAt = buffered ? now.AddHours(24) : null,
            DeliveryJson = buffered ? JsonSerializer.Serialize(diagnostic, TelemetryHttpTransport.Json) : null,
            DeliveryBytes = buffered ? 512 : 0
        });
        if (buffered)
            foreach (var owner in new[] { tenant, Guid.Empty })
            {
                var state = await db.TelemetryTenantStates.SingleOrDefaultAsync(item => item.TenantId == owner);
                if (state is null)
                {
                    state = new TelemetryTenantState { TenantId = owner, DueAt = now, LastStoredAt = now };
                    db.TelemetryTenantStates.Add(state);
                }

                state.BufferedBytes += 512;
                state.DueAt = now;
            }

        await db.SaveChangesAsync();
    }

    /// <summary>Builds independent scopes sharing an isolated real database.</summary>
    private static ServiceProvider Services(TelemetryStorageOptions? settings = null, DelayedSinks? sinks = null)
    {
        var options = Options.Create(settings ?? TelemetryStorageTests.Specialized());
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<TelemetryProjectPolicyCache>()
            .AddSingleton(TimeProvider.System)
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