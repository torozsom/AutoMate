using Application.Abstractions.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Exercises permanent archive durability, isolation, redaction and bounded replay without provider calls.</summary>
public sealed class DiskDeploymentArchiveTests
{
    /// <summary>Events retain identity after expiration/restart; duplicate delivery cannot alter saved payloads.</summary>
    [Fact]
    public async Task Restart_preserves_old_events_and_cursor_search_without_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        try
        {
            var first = Entry(tenant, project, deployment, 1, "password=private-value");
            using (var archive = Create(path))
            {
                await archive.AppendAsync(first, default);
                await archive.AppendAsync(Entry(tenant, project, deployment, 2, "next"), default);
                var duplicate =
                    await archive.AppendAsync(
                        first with { OrderId = 99, Event = first.Event with { Message = "changed" } }, default);
                Assert.Equal(1, duplicate.OrderId);
                Assert.DoesNotContain("private-value", duplicate.Event.Message);
            }

            using var reopened = Create(path);
            var recent = await reopened.ReadAsync(tenant, project, deployment, 0, true, 1, null, default);
            Assert.Equal(2, Assert.Single(recent).OrderId);
            var older = await reopened.ReadAsync(tenant, project, deployment, 2, true, 10, null, default);
            Assert.Equal(first.EventId, Assert.Single(older).EventId);
            Assert.Empty(await reopened.ReadAsync(Guid.NewGuid(), project, deployment, 0, true, 10, null, default));
            Assert.Single(await reopened.ReadAsync(tenant, project, deployment, 0, true, 10, "password", default));
            Assert.Empty(await reopened.ReadAsync(tenant, project, deployment, 0, true, 10, "private-value", default));
            Assert.Equal("0123456789abcdef0123456789abcdef", older[0].Event.TraceId);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>Bounded old metrics preserve extrema, imported intervals deduplicate and deletion prevents resurrection.</summary>
    [Fact]
    public async Task Metrics_and_deletion_are_durable_idempotent_and_bounded()
    {
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        try
        {
            using var archive = Create(path);
            var entry = Entry(tenant, project, deployment, 1, "metric");
            await archive.AppendAsync(entry with
            {
                Channel = null,
                Event = entry.Event with
                {
                    Kind = DeploymentDiagnosticKind.Metric,
                    Metrics = [new DeploymentMetricSample("automate_cpu_usage_cores", 2, "cores")],
                    TerminalChannel = new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, "web")
                }
            }, default);
            var point = new DeploymentMetricPoint("old", "automate_cpu_usage_cores", "cores", entry.Event.TimestampUtc,
                3, 1, 5);
            var import = new ArchiveMetricImport(tenant, project, deployment, [point]);
            await archive.ImportMetricsAsync(import, default);
            await archive.ImportMetricsAsync(import, default);
            var points = await archive.ReadMetricsAsync(tenant, project, deployment, point.Timestamp.AddHours(-1),
                point.Timestamp.AddHours(1), 1, default);
            Assert.Equal(2, points.Count);
            Assert.Equal(5, points.Single(p => p.Container == "old").Maximum);
            await archive.DeleteProjectAsync(tenant, project, default);
            await archive.DeleteProjectAsync(tenant, project, default);
            Assert.Empty(await archive.ReadAsync(tenant, project, deployment, 0, true, 10, null, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => archive.AppendAsync(entry, default));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>Checksum damage fails explicitly; incomplete unacknowledged temporary files do not become history.</summary>
    [Fact]
    public async Task Corruption_is_detected_and_temporary_segments_are_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-" + Guid.NewGuid().ToString("N"));
        var e = Entry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "hello");
        try
        {
            using var archive = Create(path);
            await archive.AppendAsync(e, default);
            var segment = Directory.GetFiles(path, "*.segment", SearchOption.AllDirectories).Single();
            await File.WriteAllTextAsync(segment + ".tmp", "incomplete");
            Assert.Single(await archive.ReadAsync(e.TenantId, e.Event.ProjectId, e.Event.DeploymentId!.Value, 0, true,
                10, null, default));
            await File.WriteAllTextAsync(segment, (await File.ReadAllTextAsync(segment)).Replace("hello", "tampered"));
            await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadAsync(e.TenantId, e.Event.ProjectId,
                e.Event.DeploymentId!.Value, 0, true, 10, null, default));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>Receipt availability follows archive persistence, and deletion rejection cannot acknowledge ingestion.</summary>
    [Fact]
    public async Task Spool_receipts_require_archive_persistence()
    {
        var path = Path.Combine(Path.GetTempPath(), "automate-archive-receipt-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        try
        {
            using var archive = Create(path);
            using var spool = new DiskTelemetrySpool(Options.Create(new DiskSpoolOptions { Directory = path }),
                Options.Create(new TelemetryStorageOptions()),
                NullLogger<DiskTelemetrySpool>.Instance,
                new DiagnosticRedactor(), archive);
            await spool.StartAsync(default);
            var e = Entry(tenant, project, deployment, 1, "saved").Event with { TimestampUtc = DateTimeOffset.UtcNow };
            var receipt = await spool.AppendAsync(tenant, e, "web", default);
            Assert.Equal(receipt.EventId,
                Assert.Single(await archive.ReadAsync(tenant, project, deployment, 0, true, 10, null, default))
                    .EventId);
            await archive.DeleteProjectAsync(tenant, project, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                spool.AppendAsync(tenant, e with { EventId = Guid.NewGuid() }, "web", default));
            await spool.StopAsync(default);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>Creates an isolated volume using the production redactor.</summary>
    private static DiskDeploymentArchive Create(string path)
    {
        return new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = path }),
            new DiagnosticRedactor());
    }

    /// <summary>Expired timestamps demonstrate that operational retention never filters archive reads.</summary>
    private static DeploymentLogEnvelope Entry(Guid tenant, Guid project, Guid deployment, long order, string message)
    {
        var time = DateTimeOffset.UtcNow.AddDays(-120);
        var id = Guid.NewGuid();
        return new DeploymentLogEnvelope(id, tenant, order, time, time.AddDays(30),
            new DeploymentDiagnosticEvent(project, deployment, DeploymentDiagnosticSource.DockerContainer,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, time, message,
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"),
                TraceId: "0123456789abcdef0123456789abcdef", EventId: id), "web");
    }
}