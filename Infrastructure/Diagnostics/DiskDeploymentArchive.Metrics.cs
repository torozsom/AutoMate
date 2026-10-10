using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Diagnostics;

/// <summary>Rebuildable numeric lookup; immutable checksummed archive segments remain authoritative.</summary>
public sealed partial class DiskDeploymentArchive
{
    /// <summary>Reads indexed metric payloads only, rebuilding at most 2,000 legacy segments per request.</summary>
    public async Task<ArchiveMetricBatch> ReadIndexedMetricsAsync(Guid tenant, Guid project, Guid deployment,
        MetricTimeRange range, string? container, CancellationToken token)
    {
        ValidateArchiveWindow(range);
        await _gate.WaitAsync(token);
        try
        {
            var partition = Partition(tenant, project, deployment);
            if (!Directory.Exists(partition)) return new ArchiveMetricBatch([], null);
            var index = Path.Combine(partition, "metric-index");
            Directory.CreateDirectory(index);
            var ready = Path.Combine(index, "ready");
            var incomplete = false;
            if (!File.Exists(ready))
            {
                var inspected = 0;
                foreach (var path in Directory.EnumerateFiles(partition, "*.segment")
                             .Concat(Directory.EnumerateFiles(partition, "*.metric")))
                {
                    token.ThrowIfCancellationRequested();
                    var done = Path.Combine(index, Path.GetFileName(path) + ".done");
                    if (File.Exists(done)) continue;
                    if (++inspected > 2000)
                    {
                        incomplete = true;
                        break;
                    }

                    if (path.EndsWith(".segment", StringComparison.Ordinal))
                    {
                        await IndexMetricEnvelopeAsync(await ReadSegmentAsync(path, token), token);
                    }
                    else
                    {
                        var point = JsonSerializer.Deserialize<DeploymentMetricPoint>(
                            await ReadPayloadAsync(path, token),
                            TelemetryHttpTransport.Json)!;
                        await WriteMetricReferenceAsync(partition, Path.GetFileName(path), point.Timestamp, token);
                    }

                    await File.WriteAllTextAsync(done, "1", token);
                }

                if (!incomplete) await WriteLookupManifestAsync(index, ready, token);
            }

            var buckets =
                new Dictionary<(string Container, string Metric, string Unit, DateTimeOffset Time),
                    MetricObservation>();
            var manifest = File.Exists(ready)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(await ReadPayloadAsync(ready, token))!
                : Directory.EnumerateDirectories(index).ToDictionary(day => Path.GetFileName(day),
                    day => Directory.EnumerateFiles(day, "*.ref").Count());
            foreach (var dayEntry in manifest)
            {
                var day = Path.Combine(index, dayEntry.Key);
                if (dayEntry.Key != Path.GetFileName(dayEntry.Key))
                    throw new InvalidDataException("Invalid metric day.");
                if (!DateTimeOffset.TryParseExact(dayEntry.Key, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var date) ||
                    date >= range.End || date.AddDays(1) <= range.Start) continue;
                if (!Directory.Exists(day) || Directory.EnumerateFiles(day, "*.ref").Count() != dayEntry.Value)
                    throw new InvalidDataException("Metric lookup references are incomplete.");
                foreach (var reference in Directory.EnumerateFiles(day, "*.ref"))
                {
                    token.ThrowIfCancellationRequested();
                    var name = await ReadPayloadAsync(reference, token);
                    if (name != Path.GetFileName(name) || !(name.EndsWith(".segment", StringComparison.Ordinal) ||
                                                            name.EndsWith(".metric", StringComparison.Ordinal)))
                        throw new InvalidDataException("Invalid metric lookup.");
                    var source = Path.Combine(partition, name);
                    if (name.EndsWith(".segment", StringComparison.Ordinal))
                    {
                        var e = (await ReadSegmentAsync(source, token)).Event;
                        var target = e.TerminalChannel.Target ?? "unknown";
                        if (e.TimestampUtc < range.Start || e.TimestampUtc >= range.End ||
                            (container is not null && target != container)) continue;
                        foreach (var sample in e.Metrics ?? [])
                        {
                            if (!MimirDeploymentMetrics.SupportedUnits.TryGetValue(sample.Name, out var unit) ||
                                sample.Unit != unit || !double.IsFinite(sample.Value)) continue;
                            Add(new MetricObservation(project, deployment, target, sample.Name, sample.Unit,
                                range.Bucket(e.TimestampUtc),
                                1, sample.Value, sample.Value, sample.Value));
                        }
                    }
                    else
                    {
                        var p = JsonSerializer.Deserialize<DeploymentMetricPoint>(await ReadPayloadAsync(source, token),
                            TelemetryHttpTransport.Json)!;
                        if (p.Timestamp < range.Start || p.Timestamp >= range.End ||
                            (container is not null && p.Container != container)) continue;
                        Add(new MetricObservation(project, deployment, _redactor.RedactText(p.Container, 128), p.Name,
                            p.Unit,
                            range.Bucket(p.Timestamp), 0, p.Average, p.Minimum, p.Maximum, false, 1));
                    }
                }
            }

            return new ArchiveMetricBatch(buckets.Values.OrderBy(p => p.Timestamp).ToArray(), null,
                incomplete
                    ? "Metric lookup recovery is in progress; displayed observations are partial. Refresh to continue recovery."
                    : null);

            /// <summary>Raw samples supersede imported copies without changing observed sample counts.</summary>
            void Add(MetricObservation value)
            {
                var key = (value.Container, value.Metric, value.Unit, value.Timestamp);
                if (!buckets.TryGetValue(key, out var previous))
                {
                    if (buckets.Count >= 20000)
                    {
                        incomplete = true;
                        return;
                    }

                    buckets[key] = value;
                    return;
                }

                // Raw observations supersede imported backend copies in the same bucket.
                if (previous.Samples > 0 && value.ImportedIntervals > 0) return;
                if (value.Samples > 0 && previous.ImportedIntervals > 0)
                {
                    buckets[key] = value;
                    return;
                }

                buckets[key] = previous with
                {
                    Samples = previous.Samples + value.Samples,
                    ImportedIntervals = previous.ImportedIntervals + value.ImportedIntervals,
                    Sum = previous.Sum + value.Sum, Minimum = Math.Min(previous.Minimum, value.Minimum),
                    Maximum = Math.Max(previous.Maximum, value.Maximum)
                };
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            _logger?.LogWarning(error, "Metric exploration data unavailable: {FailureType}.", error.GetType().Name);
            var index = Path.Combine(Partition(tenant, project, deployment), "metric-index");
            if (Directory.Exists(index)) Directory.Delete(index, true);
            return new ArchiveMetricBatch([], null, "Metric lookup is unavailable and will be rebuilt on refresh.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Internal day-boundary fragments may be shorter than the public five-minute window.</summary>
    private static void ValidateArchiveWindow(MetricTimeRange range)
    {
        if (range.Start.Offset != TimeSpan.Zero || range.End.Offset != TimeSpan.Zero || range.End <= range.Start ||
            range.Start < range.End.AddYears(-5) || range.End > DateTimeOffset.UtcNow)
            throw new ArgumentException("Invalid archived metric window.");
    }

    /// <summary>Seals derived day counts so missing references after a restart trigger recovery rather than silent gaps.</summary>
    private static async Task WriteLookupManifestAsync(string index, string ready, CancellationToken token)
    {
        var days = Directory.EnumerateDirectories(index).ToDictionary(day => Path.GetFileName(day),
            day => Directory.EnumerateFiles(day, "*.ref").Count());
        var payload = JsonSerializer.Serialize(days);
        var segment = new Segment(1, payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
        await File.WriteAllTextAsync(ready + ".tmp", JsonSerializer.Serialize(segment), token);
        File.Move(ready + ".tmp", ready, true);
    }

    /// <summary>Indexes a new event without storing its diagnostic message or changing the durable receipt.</summary>
    private async Task IndexMetricEnvelopeAsync(DeploymentLogEnvelope envelope, CancellationToken token)
    {
        var partition = Partition(envelope.TenantId, envelope.Event.ProjectId,
            envelope.Event.DeploymentId ?? Guid.Empty);
        if (envelope.Event.Metrics?.Count > 0)
            await WriteMetricReferenceAsync(partition, envelope.EventId.ToString("N") + ".segment",
                envelope.Event.TimestampUtc, token);
        var index = Path.Combine(partition, "metric-index");
        Directory.CreateDirectory(index);
        await File.WriteAllTextAsync(Path.Combine(index, envelope.EventId.ToString("N") + ".segment.done"), "1", token);
    }

    /// <summary>Atomically replaces a derived checksummed day lookup; its source is verified on every read.</summary>
    private static async Task WriteMetricReferenceAsync(string partition, string source, DateTimeOffset time,
        CancellationToken token)
    {
        var directory = Path.Combine(partition, "metric-index", time.UtcDateTime.ToString("yyyyMMdd"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, source + ".ref");
        var segment = new Segment(1, source, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(segment), token);
        File.Move(path + ".tmp", path, true);
    }
}