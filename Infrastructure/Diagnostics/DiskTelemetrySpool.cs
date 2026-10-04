using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

public sealed class DiskSpoolOptions
{
    public string Directory { get; set; } = "";
    public int BatchSize { get; set; } = 100;
}

/// <summary>Single-writer, checksummed immutable segments; receipts follow a flush to durable storage.</summary>
public sealed class DiskTelemetrySpool : BackgroundService
{
    private readonly int _batchSize;
    private readonly string _directory;
    private readonly Dictionary<Guid, long> _drops = [];
    private readonly SemaphoreSlim _gate = new(1);
    private readonly TelemetryStorageOptions _limits;
    private readonly ILogger<DiskTelemetrySpool> _logger;
    private readonly Dictionary<Guid, PendingIndex> _pending = [];
    private readonly Channel<PendingWrite> _queue = Channel.CreateBounded<PendingWrite>(8192);
    private readonly Dictionary<Guid, (DateTimeOffset Start, long Bytes)> _rates = [];
    private readonly Dictionary<string, DateTimeOffset> _segments = [];
    private readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> _series = [];
    private readonly Dictionary<Guid, long> _tenantBytes = [];
    private long _bytes;
    private int _deliveryPosition;
    private long _lastOrder;
    private FileStream? _writerLock;

    public DiskTelemetrySpool(IOptions<DiskSpoolOptions> spool, IOptions<TelemetryStorageOptions> limits,
        ILogger<DiskTelemetrySpool> logger)
    {
        if (!Path.IsPathFullyQualified(spool.Value.Directory))
            throw new InvalidOperationException("DiskSpool:Directory must be an absolute persistent-volume path.");
        _directory = Path.GetFullPath(spool.Value.Directory);
        _limits = limits.Value;
        _batchSize = Math.Clamp(spool.Value.BatchSize, 1, 100);
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        _writerLock = new FileStream(Path.Combine(_directory, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var clockPath = Path.Combine(_directory, "clock.json");
        if (File.Exists(clockPath)) _lastOrder = JsonSerializer.Deserialize<long>(File.ReadAllText(clockPath));
        // Complete temporary segments are safe to recover; partial writes fail closed, never silently skip records.
        foreach (var path in Directory.EnumerateFiles(_directory, "*.tmp"))
            try
            {
                _ = ReadSegment(path);
                File.Move(path, Path.ChangeExtension(path, ".segment"));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                File.Delete(path); // No receipt was issued for an incomplete temporary segment.
                _logger.LogWarning("Discarded an incomplete unacknowledged telemetry segment.");
            }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.segment").Order(StringComparer.Ordinal))
        {
            var events = ReadSegment(path);
            _segments[path] = events.Max(e => e.StoredAt).AddHours(_limits.BufferHours);
            foreach (var e in events)
            {
                _lastOrder = Math.Max(_lastOrder, e.OrderId);
                if (!_pending.TryAdd(e.EventId, Index(path, e)))
                    throw new InvalidDataException("Duplicate event identity in durable telemetry segments.");
                AddBytes(e.TenantId, Size(e));
            }
        }

        var lossPath = Path.Combine(_directory, "losses.json");
        if (File.Exists(lossPath))
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<Guid, long>>(File.ReadAllText(lossPath))!)
                _drops[pair.Key] = pair.Value;
        var seriesPath = Path.Combine(_directory, "series.json");
        if (File.Exists(seriesPath))
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<string, DateTimeOffset>>>(
                         File.ReadAllText(seriesPath))!)
                _series[pair.Key] = pair.Value;
        await base.StartAsync(cancellationToken);
    }

    public async Task<DeploymentLogEnvelope> AppendAsync(Guid tenant, DeploymentDiagnosticEvent diagnosticEvent,
        string? channel, CancellationToken token)
    {
        if (diagnosticEvent.EventId is null || diagnosticEvent.EventId == Guid.Empty)
            diagnosticEvent = diagnosticEvent with { EventId = Guid.NewGuid() };
        var completion =
            new TaskCompletionSource<DeploymentLogEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new PendingWrite(tenant, diagnosticEvent, channel, completion), token);
        // Cancellation can lose a receipt, but cannot cancel an already admitted durable write.
        return await completion.Task.WaitAsync(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                var batch = new List<PendingWrite>();
                while (batch.Count < _batchSize && _queue.Reader.TryRead(out var item)) batch.Add(item);
                await WriteBatchAsync(batch, stoppingToken);
            }
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var item))
                item.Completion.TrySetException(new IOException("Telemetry spool stopped."));
        }
    }

    private async Task WriteBatchAsync(List<PendingWrite> batch, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var accepted = new List<(PendingWrite Request, DeploymentLogEnvelope Envelope)>();
        var seriesChanged = false;
        try
        {
            foreach (var item in batch)
            {
                if (_pending.TryGetValue(item.Event.EventId!.Value, out var existing))
                {
                    if (existing.TenantId != item.Tenant || existing.ProjectId != item.Event.ProjectId ||
                        existing.DeploymentId != item.Event.DeploymentId)
                        item.Completion.TrySetException(
                            new InvalidOperationException("Event identity ownership mismatch."));
                    else
                        item.Completion.TrySetResult(ReadSegment(existing.Path)
                            .Single(e => e.EventId == item.Event.EventId));
                    continue;
                }

                var duplicate = accepted.FirstOrDefault(x => x.Envelope.EventId == item.Event.EventId);
                if (duplicate.Envelope is not null)
                {
                    item.Completion.TrySetException(
                        new InvalidOperationException("Duplicate event in ingestion batch; retry."));
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                var order = Math.Max(now.UtcTicks, _lastOrder + 10);
                _lastOrder = order;
                var envelope = new DeploymentLogEnvelope(item.Event.EventId.Value, item.Tenant, order,
                    new DateTimeOffset(order, TimeSpan.Zero), now.AddDays(30), item.Event, item.Channel);
                var size = Size(envelope);
                var identity = $"{item.Event.DeploymentId:N}/{item.Event.TerminalChannel.Target}";
                if (!_series.TryGetValue(item.Tenant, out var identities)) _series[item.Tenant] = identities = [];
                foreach (var expired in identities.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                    identities.Remove(expired);
                var rate = _rates.GetValueOrDefault(item.Tenant);
                if (rate.Start <= now.AddMinutes(-1)) rate = (now, 0);
                if (_bytes + size > _limits.GlobalBufferBytes ||
                    _tenantBytes.GetValueOrDefault(item.Tenant) + size > _limits.TenantBufferBytes ||
                    rate.Bytes + size > _limits.TenantBytesPerMinute ||
                    (item.Event.Metrics is { Count: > 0 } && !identities.ContainsKey(identity) &&
                     identities.Count >= _limits.MaximumMetricContainers))
                {
                    RecordLoss(item.Tenant, 1);
                    TelemetryStorageMetrics.Dropped.Add(1);
                    item.Completion.TrySetException(new TelemetryProviderException(429, TimeSpan.FromSeconds(2)));
                    continue;
                }

                _rates[item.Tenant] = (rate.Start, rate.Bytes + size);
                if (item.Event.Metrics is { Count: > 0 })
                {
                    identities[identity] = now.AddDays(30);
                    seriesChanged = true;
                }

                AddBytes(item.Tenant, size);
                accepted.Add((item, envelope));
            }

            if (accepted.Count == 0) return;
            var final = Path.Combine(_directory, $"{accepted[0].Envelope.OrderId:D19}-{Guid.NewGuid():N}.segment");
            var temporary = Path.ChangeExtension(final, ".tmp");
            var events = accepted.Select(x => x.Envelope).ToArray();
            var payload = JsonSerializer.Serialize(events, TelemetryHttpTransport.Json);
            var record = new Segment(1, payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, record, cancellationToken: token);
                await file.FlushAsync(token);
                file.Flush(true);
            }

            File.Move(temporary, final);
            if (seriesChanged) WriteState("series.json", _series);
            WriteState("clock.json", _lastOrder);
            _segments[final] = events.Max(e => e.StoredAt).AddHours(_limits.BufferHours);
            foreach (var item in accepted)
            {
                _pending.Add(item.Envelope.EventId, Index(final, item.Envelope));
                item.Request.Completion.TrySetResult(item.Envelope);
            }
        }
        catch (Exception ex)
        {
            foreach (var item in accepted) AddBytes(item.Envelope.TenantId, -Size(item.Envelope));
            foreach (var item in batch) item.Completion.TrySetException(ex);
            // Fail closed after an uncertain disk write; recovery resolves temporary segments on restart.
            _queue.Writer.TryComplete(ex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> SegmentPathsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var paths = _segments.Keys.Order(StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return [];
            var start = _deliveryPosition % paths.Length;
            _deliveryPosition = (start + Math.Min(32, paths.Length)) % paths.Length;
            return paths.Skip(start).Concat(paths.Take(start)).Take(32).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<DeploymentLogEnvelope> ReadSegment(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _directory,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Telemetry segment escaped the spool directory.");
        var segment = JsonSerializer.Deserialize<Segment>(File.ReadAllText(path)) ??
                      throw new InvalidDataException("Empty segment.");
        if (segment.Version != 1 || segment.Checksum !=
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segment.Payload))))
            throw new InvalidDataException("Telemetry segment checksum/version mismatch.");
        return JsonSerializer.Deserialize<DeploymentLogEnvelope[]>(segment.Payload, TelemetryHttpTransport.Json) ??
               throw new InvalidDataException("Invalid telemetry segment.");
    }

    public async Task PurgeExpiredAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string[] expired;
        try
        {
            expired = _segments.Where(p => p.Value <= DateTimeOffset.UtcNow).Select(p => p.Key).Take(512).ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var path in expired)
        {
            var events = ReadSegment(path);
            await RemoveAsync(path, events, token);
            TelemetryStorageMetrics.Dropped.Add(events.Count);
        }
    }

    public async Task<object> StatusAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            return new
            {
                pendingEvents = _pending.Count,
                bufferedBytes = _bytes,
                segments = _segments.Count,
                oldestPendingSeconds = _pending.Count == 0
                    ? 0
                    : Math.Max(0, (DateTimeOffset.UtcNow - _pending.Values.Min(e => e.StoredAt)).TotalSeconds),
                droppedEvents = _drops.Values.Sum(),
                accepting = !_queue.Reader.Completion.IsCompleted
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string path, IEnumerable<DeploymentLogEnvelope> lost, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_segments.ContainsKey(path)) return;
            foreach (var group in lost.GroupBy(e => e.TenantId)) RecordLoss(group.Key, group.LongCount());
            var events = ReadSegment(path);
            File.Delete(path);
            foreach (var e in events)
                if (_pending.Remove(e.EventId))
                    AddBytes(e.TenantId, -Size(e));
            _segments.Remove(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelemetryPendingHistory> PendingAsync(Guid tenant, Guid project, Guid deployment,
        CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var indexes = _pending.Values.Where(e => e.TenantId == tenant && e.ProjectId == project &&
                                                     e.DeploymentId == deployment &&
                                                     e.StoredAt.AddHours(_limits.BufferHours) > DateTimeOffset.UtcNow)
                .OrderByDescending(e => e.OrderId).Take(2001).ToArray();
            var events = new List<DeploymentLogEnvelope>();
            foreach (var group in indexes.Take(2000).GroupBy(e => e.Path))
            {
                var ids = group.Select(e => e.EventId).ToHashSet();
                events.AddRange(ReadSegment(group.Key).Where(e => ids.Contains(e.EventId)));
            }

            return new TelemetryPendingHistory(events.OrderBy(e => e.OrderId).ToArray(),
                _drops.GetValueOrDefault(tenant), indexes.Length > 2000);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RecordLoss(Guid tenant, long count)
    {
        _drops[tenant] = _drops.GetValueOrDefault(tenant) + count;
        WriteState("losses.json", _drops);
    }

    private void WriteState<T>(string name, T state)
    {
        var path = Path.Combine(_directory, name);
        using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state);
            file.Flush(true);
        }

        File.Move(path + ".new", path, true);
    }

    private static int Size(DeploymentLogEnvelope e)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            JsonSerializer.Serialize(e, TelemetryHttpTransport.Json)).Length + 128;
    }

    private static PendingIndex Index(string path, DeploymentLogEnvelope e)
    {
        return new PendingIndex(path, e.EventId, e.TenantId,
            e.Event.ProjectId, e.Event.DeploymentId, e.OrderId, e.StoredAt);
    }

    private void AddBytes(Guid tenant, long bytes)
    {
        _bytes += bytes;
        _tenantBytes[tenant] = _tenantBytes.GetValueOrDefault(tenant) + bytes;
    }

    public override void Dispose()
    {
        base.Dispose();
        _writerLock?.Dispose();
        _gate.Dispose();
    }

    private sealed record PendingWrite(
        Guid Tenant,
        DeploymentDiagnosticEvent Event,
        string? Channel,
        TaskCompletionSource<DeploymentLogEnvelope> Completion);

    private sealed record Segment(int Version, string Payload, string Checksum);

    private sealed record PendingIndex(
        string Path,
        Guid EventId,
        Guid TenantId,
        Guid ProjectId,
        Guid? DeploymentId,
        long OrderId,
        DateTimeOffset StoredAt);
}