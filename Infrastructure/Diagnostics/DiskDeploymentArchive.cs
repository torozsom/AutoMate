using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Checksummed immutable per-event segments on the telemetry volume, retained until owner deletion.</summary>
public sealed class DiskDeploymentArchive : IDeploymentArchive, IDisposable
{
    /// <summary>Serializes writes, queries and deletion within the volume's single-writer host.</summary>
    private readonly SemaphoreSlim _gate = new(1);

    /// <summary>Current mandatory read/write masking policy.</summary>
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Archive partition root, isolated from pending-spool cleanup.</summary>
    private readonly string _root;

    /// <summary>Uses a child of the existing persistent spool volume.</summary>
    public DiskDeploymentArchive(IOptions<DiskSpoolOptions> options, IDiagnosticRedactor redactor)
    {
        if (!Path.IsPathFullyQualified(options.Value.Directory))
            throw new InvalidOperationException("Archive requires an absolute persistent spool directory.");
        _root = Path.Combine(Path.GetFullPath(options.Value.Directory), "archive");
        _redactor = redactor;
    }

    /// <inheritdoc />
    public async Task<DeploymentLogEnvelope> AppendAsync(DeploymentLogEnvelope envelope, CancellationToken token)
    {
        if (envelope.TenantId == Guid.Empty || envelope.Event.ProjectId == Guid.Empty || envelope.EventId == Guid.Empty)
            throw new ArgumentException("Archive identities must be nonempty.");
        await _gate.WaitAsync(token);
        try
        {
            var directory = Partition(envelope.TenantId, envelope.Event.ProjectId,
                envelope.Event.DeploymentId ?? Guid.Empty);
            if (File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, ".deleted")))
                throw new InvalidOperationException("Deleted project cannot accept archive events.");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, envelope.EventId.ToString("N") + ".segment");
            if (File.Exists(path)) return await ReadSegmentAsync(path, token);
            envelope = Mask(envelope);
            var payload = JsonSerializer.Serialize(envelope, TelemetryHttpTransport.Json);
            var segment = new Segment(1, payload,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
            var temporary = path + ".tmp";
            // A previous incomplete, unacknowledged archive write may be replaced by its idempotent retry.
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, segment, cancellationToken: token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }

            File.Move(temporary, path);
            return envelope;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string? search, CancellationToken token)
    {
        limit = Math.Clamp(limit, 1, 2001);
        if (search?.Length > 256) throw new ArgumentException("Archive search exceeds its bound.");
        await _gate.WaitAsync(token);
        try
        {
            // Retain only the requested page in memory, even when a partition contains years of output.
            var page = new SortedDictionary<long, DeploymentLogEnvelope>();
            foreach (var path in Files(tenant, project, deployment))
            {
                token.ThrowIfCancellationRequested();
                var entry = await ReadSegmentAsync(path, token);
                if (entry.Channel is null || entry.Event.Kind == DeploymentDiagnosticKind.Metric ||
                    (cursor != 0 && (backwards ? entry.OrderId >= cursor : entry.OrderId <= cursor)) ||
                    (!string.IsNullOrEmpty(search) &&
                     !entry.Event.Message.Contains(search, StringComparison.Ordinal))) continue;
                page.TryAdd(entry.OrderId, entry);
                if (page.Count > limit) page.Remove(backwards ? page.First().Key : page.Last().Key);
            }

            return page.Values.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentMetricPoint>> ReadMetricsAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token)
    {
        if (end <= start) throw new ArgumentException("Metric range must be positive.");
        maximumPoints = Math.Clamp(maximumPoints, 1, 1000);
        var interval = Math.Max(60, Math.Ceiling((end - start).TotalSeconds / maximumPoints));
        await _gate.WaitAsync(token);
        try
        {
            var buckets = new Dictionary<(string Container, string Name, string Unit, long Bucket), Aggregate>();
            foreach (var path in Files(tenant, project, deployment))
            {
                token.ThrowIfCancellationRequested();
                var entry = await ReadSegmentAsync(path, token);
                var time = entry.Event.TimestampUtc;
                if (time < start || time > end) continue;
                foreach (var metric in entry.Event.Metrics ?? [])
                {
                    if (!double.IsFinite(metric.Value)) continue;
                    var bucket = Math.Min(maximumPoints - 1, (long)((time - start).TotalSeconds / interval));
                    var key = (entry.Event.TerminalChannel.Target ?? "unknown", metric.Name, metric.Unit, bucket);
                    if (!buckets.TryGetValue(key, out var aggregate))
                    {
                        if (buckets.Count >= 300_000)
                            throw new InvalidOperationException("Narrow the archived metric range.");
                        buckets[key] = aggregate = new Aggregate();
                    }

                    aggregate.Add(metric.Value);
                }
            }

            var rawKeys = buckets.Keys.ToHashSet();
            var directory = Partition(tenant, project, deployment);
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.metric"))
                {
                    var point = JsonSerializer.Deserialize<DeploymentMetricPoint>(await ReadPayloadAsync(path, token),
                        TelemetryHttpTransport.Json)!;
                    if (point.Timestamp < start || point.Timestamp > end) continue;
                    var bucket = Math.Min(maximumPoints - 1, (long)((point.Timestamp - start).TotalSeconds / interval));
                    var key = (point.Container, point.Name, point.Unit, bucket);
                    if (rawKeys.Contains(key))
                        continue; // Raw samples already persisted by live ingestion take precedence.
                    if (!buckets.TryGetValue(key, out var aggregate))
                    {
                        if (buckets.Count >= 300_000)
                            throw new InvalidOperationException("Narrow the archived metric range.");
                        buckets[key] = aggregate = new Aggregate();
                    }

                    aggregate.Add(point.Average, point.Minimum, point.Maximum);
                }

            return buckets.Select(p => new DeploymentMetricPoint(p.Key.Container, p.Key.Name, p.Key.Unit,
                    start.AddSeconds(p.Key.Bucket * interval), p.Value.Sum / p.Value.Count, p.Value.Minimum,
                    p.Value.Maximum))
                .OrderBy(p => p.Timestamp).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ImportMetricsAsync(ArchiveMetricImport import, CancellationToken token)
    {
        if (import.Tenant == Guid.Empty || import.Project == Guid.Empty || import.Deployment == Guid.Empty ||
            import.Points.Count > 3000) throw new ArgumentException("Invalid bounded metric import.");
        await _gate.WaitAsync(token);
        try
        {
            var directory = Partition(import.Tenant, import.Project, import.Deployment);
            if (File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, ".deleted")))
                throw new InvalidOperationException("Deleted project cannot accept archive events.");
            Directory.CreateDirectory(directory);
            foreach (var original in import.Points)
            {
                if (!MimirDeploymentMetrics.SupportedUnits.TryGetValue(original.Name, out var unit) ||
                    unit != original.Unit ||
                    !double.IsFinite(original.Average) || !double.IsFinite(original.Minimum) ||
                    !double.IsFinite(original.Maximum) ||
                    original.Minimum < 0 || original.Minimum > original.Average ||
                    original.Average > original.Maximum ||
                    original.Timestamp > DateTimeOffset.UtcNow.AddMinutes(5))
                    throw new ArgumentException("Invalid metric statistics.");
                var point = original with { Container = _redactor.RedactText(original.Container, 128) };
                var identity = $"{point.Container}|{point.Name}|{point.Timestamp.UtcTicks}";
                var path = Path.Combine(directory,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".metric");
                if (File.Exists(path))
                {
                    await ReadPayloadAsync(path, token);
                    continue;
                }

                var payload = JsonSerializer.Serialize(point, TelemetryHttpTransport.Json);
                var segment = new Segment(1, payload,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
                await using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write,
                                 FileShare.None,
                                 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, segment, cancellationToken: token);
                    await stream.FlushAsync(token);
                    stream.Flush(true);
                }

                File.Move(path + ".tmp", path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteProjectAsync(Guid tenant, Guid project, CancellationToken token)
    {
        if (tenant == Guid.Empty || project == Guid.Empty)
            throw new ArgumentException("Invalid archive deletion identity.");
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(_root, tenant.ToString("N"), project.ToString("N"));
            if (Directory.Exists(path)) Directory.Delete(path, true);
            var checkpoint = Path.Combine(Path.GetDirectoryName(_root)!, "archive-backfill", tenant.ToString("N"),
                project.ToString("N"));
            if (Directory.Exists(checkpoint)) Directory.Delete(checkpoint, true);
            Directory.CreateDirectory(path);
            // Retain a tiny durable tombstone so an in-flight cached collector cannot recreate deleted payloads.
            using var marker = new FileStream(Path.Combine(path, ".deleted"), FileMode.Create, FileAccess.Write,
                FileShare.None);
            marker.Flush(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>Constructs paths exclusively from typed GUIDs, never provider names or user paths.</summary>
    private string Partition(Guid tenant, Guid project, Guid deployment)
    {
        return Path.Combine(_root, tenant.ToString("N"), project.ToString("N"), deployment.ToString("N"));
    }

    /// <summary>Enumerates segment names without loading their payloads into memory.</summary>
    private IEnumerable<string> Files(Guid tenant, Guid project, Guid deployment)
    {
        var directory = Partition(tenant, project, deployment);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.segment") : [];
    }

    /// <summary>Checks the immutable checksum before decoding and applying current redaction.</summary>
    private async Task<DeploymentLogEnvelope> ReadSegmentAsync(string path, CancellationToken token)
    {
        return Mask(JsonSerializer.Deserialize<DeploymentLogEnvelope>(await ReadPayloadAsync(path, token),
                        TelemetryHttpTransport.Json)
                    ?? throw new InvalidDataException("Missing archive envelope."));
    }

    /// <summary>Verifies both log and imported-metric segments before decoding.</summary>
    private static async Task<string> ReadPayloadAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("Archive segment exceeds its bound.");
        var segment = JsonSerializer.Deserialize<Segment>(await File.ReadAllTextAsync(path, token))
                      ?? throw new InvalidDataException("Missing archive segment.");
        if (segment.Version != 1 || segment.Checksum !=
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segment.Payload))))
            throw new InvalidDataException("Archive checksum mismatch.");
        return segment.Payload;
    }

    /// <summary>Detaches and masks payloads at both persistence and replay boundaries.</summary>
    private DeploymentLogEnvelope Mask(DeploymentLogEnvelope entry)
    {
        return entry with
        {
            Event = _redactor.Redact(entry.Event with { EventId = entry.EventId }).Event,
            Channel = entry.Channel is null ? null : _redactor.RedactText(entry.Channel, 128)
        };
    }

    /// <summary>Versioned checksummed disk record; contains only normalized redacted diagnostics.</summary>
    private sealed record Segment(int Version, string Payload, string Checksum);

    /// <summary>Constant-memory statistics for one chart bucket.</summary>
    private sealed class Aggregate
    {
        /// <summary>Observed sample count.</summary>
        public long Count;

        /// <summary>Largest observation.</summary>
        public double Maximum = double.NegativeInfinity;

        /// <summary>Smallest observation.</summary>
        public double Minimum = double.PositiveInfinity;

        /// <summary>Sum of observed samples.</summary>
        public double Sum;

        /// <summary>Accumulates one finite sample.</summary>
        public void Add(double value)
        {
            Add(value, value, value);
        }

        /// <summary>Accumulates retained interval statistics without inventing raw weighting.</summary>
        public void Add(double mean, double minimum, double maximum)
        {
            Sum += mean;
            Count++;
            Minimum = Math.Min(Minimum, minimum);
            Maximum = Math.Max(Maximum, maximum);
        }
    }
}