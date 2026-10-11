using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Abstractions.Diagnostics;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

public sealed class DiskTelemetrySpoolTests
{
    /// <summary>Old checksummed backlog is masked for replay/delivery without altering its acknowledged disk bytes.</summary>
    [Fact]
    public async Task Existing_segment_is_masked_on_read_without_rewriting_or_ignoring_checksum()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var spool = Create(directory))
            {
                await spool.StartAsync(default);
                await spool.AppendAsync(Guid.NewGuid(), Event(), "web", default);
                await spool.StopAsync(default);
            }

            var path = Directory.GetFiles(directory, "*.segment").Single();
            var segment = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var payload = segment["Payload"]!.GetValue<string>()
                .Replace("hello", "password=private-backlog", StringComparison.Ordinal);
            segment["Payload"] = payload;
            segment["Checksum"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var original = segment.ToJsonString();
            await File.WriteAllTextAsync(path, original);
            using var restarted = Create(directory);
            await restarted.StartAsync(default);
            var envelope = Assert.Single(restarted.ReadSegment(path));
            Assert.Equal("password=[REDACTED]", envelope.Event.Message);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            await restarted.StopAsync(default);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Actual publisher measurements cannot turn arbitrary numeric enum inputs into unbounded metric dimensions.</summary>
    [Fact]
    public async Task Publisher_metric_labels_use_finite_values_for_malformed_external_enums()
    {
        _ = AutoMateTelemetry.DeploymentMeter;
        var observed = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "automate.deployment.diagnostics.received")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => observed.Enqueue(tags.ToArray()));
        listener.Start();
        var publisher = new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(new DeploymentDiagnosticOptions()), NullLogger<DeploymentDiagnosticPublisher>.Instance);
        var e = Event() with
        {
            Source = (DeploymentDiagnosticSource)123456,
            Kind = (DeploymentDiagnosticKind)123456,
            Severity = (DeploymentDiagnosticSeverity)123456,
            TerminalChannel = new DeploymentTerminalChannel((DeploymentTerminalChannelKind)123456),
            Message = "password=private-secret"
        };
        await publisher.PublishAsync(e);
        Assert.Contains(observed, tags => tags.Length == 4 && tags.All(pair => Equals(pair.Value, "Unknown")));
        Assert.True(publisher.Reader.TryRead(out var queued));
        Assert.Equal("password=[REDACTED]", queued.Message);
    }

    /// <summary>Direct spool callers cannot queue mutable/raw secrets; receipts and restart replay retain safe identity.</summary>
    [Fact]
    public async Task Direct_queue_admission_masks_payload_and_channel_before_durable_write()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-spool-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var attributes = new Dictionary<string, string> { ["Authorization"] = "Bearer private-secret" };
        var e = Event() with { Message = "password=private-password", Attributes = attributes };
        try
        {
            using (var spool = Create(directory))
            {
                await spool.StartAsync(default);
                var writing = spool.AppendAsync(tenant, e, "token=private-channel", default);
                attributes["Authorization"] = "Bearer private-mutated";
                var receipt = await writing;
                Assert.Equal(e.EventId, receipt.EventId);
                Assert.Equal(e.ProjectId, receipt.Event.ProjectId);
                Assert.DoesNotContain("private", JsonSerializer.Serialize(receipt));
                Assert.Equal("password=[REDACTED]", receipt.Event.Message);
                await spool.StopAsync(default);
            }

            Assert.DoesNotContain("private",
                await File.ReadAllTextAsync(Directory.GetFiles(directory, "*.segment").Single()));
            using var restarted = Create(directory);
            await restarted.StartAsync(default);
            var pending = await restarted.PendingAsync(tenant, e.ProjectId, e.DeploymentId!.Value, default);
            Assert.DoesNotContain("private", JsonSerializer.Serialize(pending));
            Assert.Single(pending.Events);
            await restarted.StopAsync(default);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Receipts_survive_restart_and_retry_keeps_identity_and_position()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-spool-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var e = Event();
        DeploymentLogEnvelope receipt;
        try
        {
            using (var spool = Create(directory))
            {
                await spool.StartAsync(default);
                receipt = await spool.AppendAsync(tenant, e, "web", default);
                Assert.Single(Directory.GetFiles(directory, "*.segment"));
                Assert.Equal(e.EventId, receipt.EventId);
                Assert.Equal(receipt, await spool.AppendAsync(tenant, e, "web", default));
                await spool.StopAsync(default);
            }

            using (var restarted = Create(directory))
            {
                await restarted.StartAsync(default);
                Assert.Equal(receipt, await restarted.AppendAsync(tenant, e, "web", default));
                Assert.Empty((await restarted.PendingAsync(Guid.NewGuid(), e.ProjectId, e.DeploymentId!.Value, default))
                    .Events);
                await restarted.StopAsync(default);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Corrupt_acknowledged_segment_fails_closed_and_preserves_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var spool = Create(directory))
            {
                await spool.StartAsync(default);
                await spool.AppendAsync(Guid.NewGuid(), Event(), "web", default);
                await spool.StopAsync(default);
            }

            var segment = Directory.GetFiles(directory, "*.segment").Single();
            var json = await File.ReadAllTextAsync(segment);
            await File.WriteAllTextAsync(segment, json.Replace("hello", "broken", StringComparison.Ordinal));
            using var restarted = Create(directory);
            await Assert.ThrowsAsync<InvalidDataException>(() => restarted.StartAsync(default));
            Assert.True(File.Exists(segment));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Buffer_limit_is_durable_and_does_not_admit_an_unconfirmed_payload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-spool-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        try
        {
            using var spool = Create(directory,
                new TelemetryStorageOptions { TenantBufferBytes = 1, GlobalBufferBytes = 1 });
            await spool.StartAsync(default);
            var e = Event();
            await Assert.ThrowsAsync<TelemetryProviderException>(() => spool.AppendAsync(tenant, e, "web", default));
            var pending = await spool.PendingAsync(tenant, e.ProjectId, e.DeploymentId!.Value, default);
            Assert.Empty(pending.Events);
            Assert.Equal(1, pending.DroppedEvents);
            Assert.True(File.Exists(Path.Combine(directory, "losses.json")));
            await spool.StopAsync(default);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Only_one_writer_can_open_the_volume()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = Create(directory);
            using var second = Create(directory);
            await first.StartAsync(default);
            await Assert.ThrowsAsync<IOException>(() => second.StartAsync(default));
            await first.StopAsync(default);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void V2_cursor_is_versioned_and_bound_to_project_and_deployment()
    {
        var p = Guid.NewGuid();
        var d = Guid.NewGuid();
        var cursor = TelemetryHistoryCursor.Encode(p, d, 123);
        Assert.Equal(123, TelemetryHistoryCursor.Decode(cursor, p, d));
        Assert.Throws<ArgumentException>(() => TelemetryHistoryCursor.Decode(cursor, Guid.NewGuid(), d));
        Assert.Throws<ArgumentException>(() => TelemetryHistoryCursor.Decode("garbage", p, d));
    }

    [Fact]
    public async Task Pilot_load_300_deployments_100_logs_per_second_with_1000_per_second_burst()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-load-" + Guid.NewGuid().ToString("N"));
        var deployments = Enumerable.Range(0, 300).Select(_ => (Tenant: Guid.NewGuid(), Event: Event())).ToArray();
        try
        {
            using var spool = Create(directory, new TelemetryStorageOptions { GlobalBufferBytes = 64 * 1024 * 1024 });
            await spool.StartAsync(default);
            var watch = Stopwatch.StartNew();
            var writes = new List<Task<DeploymentLogEnvelope>>();
            var sustained = Environment.GetEnvironmentVariable("AUTOMATE_TELEMETRY_EXTENDED_LOAD") == "1" ? 60 : 5;
            var burst = sustained == 60 ? 10 : 2;
            var expected = sustained * 100 + burst * 1000;
            // The opt-in extended benchmark retains every durable receipt across one minute plus a burst.
            for (var second = 0; second < sustained + burst; second++)
            {
                var rate = second < sustained ? 100 : 1000;
                for (var i = 0; i < rate; i++)
                {
                    var target = deployments[writes.Count % deployments.Length];
                    writes.Add(spool.AppendAsync(target.Tenant, target.Event with
                    {
                        EventId = Guid.NewGuid(),
                        Message = new string('x', 1000)
                    }, "web", default));
                }

                var delay = TimeSpan.FromSeconds(second + 1) - watch.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
            }

            var receipts = await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(expected, receipts.Length);
            Assert.Equal(expected, receipts.Select(r => r.EventId).Distinct().Count());
            Assert.Equal(expected, receipts.Select(r => r.OrderId).Distinct().Count());
            Assert.Equal(300, receipts.Select(r => r.Event.DeploymentId).Distinct().Count());
            await spool.StopAsync(default);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "telemetry-load-result.txt"),
                $"300 deployments; {expected} durable log receipts; 100/s for {sustained}s + 1000/s for {burst}s; elapsed={watch.Elapsed.TotalSeconds:F3}s; segmentFiles={Directory.GetFiles(directory, "*.segment").Length}");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static DiskTelemetrySpool Create(string directory, TelemetryStorageOptions? limits = null)
    {
        return new DiskTelemetrySpool(Options.Create(new DiskSpoolOptions { Directory = directory }),
            Options.Create(limits ?? new TelemetryStorageOptions()),
            NullLogger<DiskTelemetrySpool>.Instance, new DiagnosticRedactor());
    }

    private static DeploymentDiagnosticEvent Event()
    {
        return new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(), DeploymentDiagnosticSource.DockerContainer,
            DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, "hello",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"), EventId: Guid.NewGuid());
    }
}