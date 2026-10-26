using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Diagnostics.Redaction;
using Infrastructure.Diagnostics.Delivery;
using Infrastructure.Diagnostics.Metrics;
using Infrastructure.Diagnostics.Policy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics.Storage;

/// <summary>
///     Configures the private single-writer spool volume and bounded write-batch size.
/// </summary>
public sealed class DiskSpoolOptions
{
    /// <summary>Absolute persistent-volume path for the private spool; one writer owns the volume.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Maximum pending events flushed together before durable acknowledgement.</summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>
///     Single-writer, checksummed immutable segments; receipts follow a flush to durable
///     storage.
/// </summary>
public sealed class DiskTelemetrySpool : BackgroundService
{
    /// <summary>
    ///     Permanent history is required by the production telemetry composition before issuing
    ///     receipts.
    /// </summary>
    private readonly IDeploymentArchive? _archive;

    /// <summary>Clamped maximum pending events per write batch.</summary>
    private readonly int _batchSize;

    /// <summary>Absolute verified persistent spool directory.</summary>
    private readonly string _directory;

    /// <summary>Conservative persisted lost-event totals by owner account.</summary>
    private readonly Dictionary<Guid, long> _drops = [];

    /// <summary>Serializes spool indexes, volume budgets and persistent metadata updates.</summary>
    private readonly SemaphoreSlim _gate = new(1);

    /// <summary>Configured validated storage and ingestion bounds.</summary>
    private readonly TelemetryStorageOptions _limits;

    /// <summary>Reviewed operational logger without diagnostic payloads.</summary>
    private readonly ILogger<DiskTelemetrySpool> _logger;

    /// <summary>Memory-only event indexes; payloads remain in immutable checksummed segments.</summary>
    private readonly Dictionary<Guid, PendingIndex> _pending = [];

    /// <summary>Bounded pending-event channel awaiting durable write acknowledgement.</summary>
    private readonly Channel<PendingWrite> _queue = Channel.CreateBounded<PendingWrite>(8192);

    /// <summary>Per-tenant ingestion-window byte accounting.</summary>
    private readonly Dictionary<Guid, (DateTimeOffset Start, long Bytes)> _rates = [];

    /// <summary>Shared masking policy applied before diagnostic payloads enter the spool queue.</summary>
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Known immutable segment paths and retention deadlines.</summary>
    private readonly Dictionary<string, DateTimeOffset> _segments = [];

    /// <summary>Bounded per-tenant metric identity expiry metadata.</summary>
    private readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> _series = [];

    /// <summary>Buffered-byte budget charged to each tenant.</summary>
    private readonly Dictionary<Guid, long> _tenantBytes = [];

    /// <summary>Total buffered encoded bytes charged against the global volume budget.</summary>
    private long _bytes;

    /// <summary>Round-robin position for bounded segment selection.</summary>
    private int _deliveryPosition;

    /// <summary>Latest assigned durable spool ordering position.</summary>
    private long _lastOrder;

    /// <summary>Exclusive volume lock preventing multiple spool writers.</summary>
    private FileStream? _writerLock;

    /// <summary>
    ///     Creates the single writer with mandatory queue-admission redaction.
    /// </summary>
    /// <param name="spool">Provides the configured limits and endpoint settings for this service.</param>
    /// <param name="limits">Provides the configured limits and endpoint settings for this service.</param>
    /// <param name="logger">
    ///     Receives reviewed operational events without diagnostic payloads or credentials.
    /// </param>
    /// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
    /// <param name="archive">Provides the deployment archive? boundary used by this operation.</param>
    /// <exception cref="InvalidOperationException">
    ///     DiskSpool:Directory must be an absolute persistent-volume path.
    /// </exception>
    public DiskTelemetrySpool(IOptions<DiskSpoolOptions> spool, IOptions<TelemetryStorageOptions> limits,
        ILogger<DiskTelemetrySpool> logger, IDiagnosticRedactor redactor, IDeploymentArchive? archive = null)
    {
        if (!Path.IsPathFullyQualified(spool.Value.Directory))
            throw new InvalidOperationException("DiskSpool:Directory must be an absolute persistent-volume path.");
        _directory = Path.GetFullPath(spool.Value.Directory);
        _limits = limits.Value;
        _batchSize = Math.Clamp(spool.Value.BatchSize, 1, 100);
        _logger = logger;
        _redactor = redactor;
        _archive = archive;
    }

    /// <summary>
    ///     Acquires exclusive spool ownership and restores verified pending segment metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="InvalidDataException">
    ///     Duplicate event identity in durable telemetry segments.
    /// </exception>
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

    /// <summary>
    ///     Snapshots and redacts payloads before admission, preserving identity and durable receipt
    ///     semantics.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="channel">The stable terminal channel used for routing.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the immutable redacted delivery envelope with its stable identity.</returns>
    public async Task<DeploymentLogEnvelope> AppendAsync(Guid tenant, DeploymentDiagnosticEvent diagnosticEvent,
        string? channel, CancellationToken token)
    {
        diagnosticEvent = _redactor.Redact(diagnosticEvent).Event;
        channel = channel is null ? null : _redactor.RedactText(channel, 128);
        if (diagnosticEvent.EventId is null || diagnosticEvent.EventId == Guid.Empty)
            diagnosticEvent = diagnosticEvent with { EventId = Guid.NewGuid() };
        var completion =
            new TaskCompletionSource<DeploymentLogEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new PendingWrite(tenant, diagnosticEvent, channel, completion), token);
        // Cancellation can lose a receipt, but cannot cancel an already admitted durable write.
        return await completion.Task.WaitAsync(token);
    }

    /// <summary>
    ///     Supervises bounded single-writer batches and durable acknowledgement completion.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
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

    /// <summary>
    ///     Writes immutable checksummed segments and fulfils receipts only after durable
    ///     acknowledgement.
    /// </summary>
    /// <param name="batch">Bounded pending writes to flush before acknowledging receipts.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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
                    {
                        item.Completion.TrySetException(
                            new InvalidOperationException("Event identity ownership mismatch."));
                    }
                    else
                    {
                        var receipt = ReadSegment(existing.Path).Single(e => e.EventId == item.Event.EventId);
                        if (_archive is not null) receipt = await _archive.AppendAsync(receipt, token);
                        item.Completion.TrySetResult(receipt);
                    }

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

                if (_archive is not null) envelope = await _archive.AppendAsync(envelope, token);
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

    /// <summary>
    ///     Returns a bounded selection of pending segment paths for delivery.
    /// </summary>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the segment paths async selection with its documented bounds and
    ///     ordering.
    /// </returns>
    public async Task<IReadOnlyList<string>> SegmentPathsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var paths = _segments.Keys.Order(StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return [];
            var start = _deliveryPosition % paths.Length;
            _deliveryPosition = (start + Math.Min(32, paths.Length)) % paths.Length;
            return [.. paths.Skip(start).Concat(paths.Take(start)).Take(32)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Verifies immutable disk bytes, then applies current masking to backlog delivery/replay
    ///     without rewriting files.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <returns>The segment selection with its documented bounds and ordering.</returns>
    /// <exception cref="InvalidOperationException">Telemetry segment escaped the spool directory.</exception>
    /// <exception cref="InvalidDataException">Empty segment.</exception>
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
        var events =
            JsonSerializer.Deserialize<DeploymentLogEnvelope[]>(segment.Payload, TelemetryHttpTransport.Json) ??
            throw new InvalidDataException("Invalid telemetry segment.");
        return
        [
            .. events.Select(envelope => envelope with
            {
                Event = _redactor.Redact(envelope.Event).Event,
                Channel = envelope.Channel is null ? null : _redactor.RedactText(envelope.Channel, 128)
            })
        ];
    }

    /// <summary>
    ///     Removes expired segments while preserving loss accounting and pending-budget
    ///     consistency.
    /// </summary>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public async Task PurgeExpiredAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string[] expired;
        try
        {
            expired = [.. _segments.Where(p => p.Value <= DateTimeOffset.UtcNow).Select(p => p.Key).Take(512)];
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

    /// <summary>
    ///     Reads bounded queue, volume and loss metadata without exposing diagnostic payloads.
    /// </summary>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning queue counts, buffered bytes, segment count, oldest age and
    ///     conservative loss totals.
    /// </returns>
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

    /// <summary>
    ///     Removes a confirmed or expired segment and updates byte and loss accounting.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="lost">Acknowledged envelopes counted as lost when their segments are removed.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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

    /// <summary>
    ///     Reads bounded redacted pending events for an exact tenant/project/deployment scope.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The owned project identity or normalized local import metadata.</param>
    /// <param name="deployment">The exact deployment identity or tracked metadata for this operation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning bounded redacted pending events and explicit omission/loss metadata.</returns>
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

            return new TelemetryPendingHistory([.. events.OrderBy(e => e.OrderId)],
                _drops.GetValueOrDefault(tenant), indexes.Length > 2000);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Adds dropped-event counts to durable conservative tenant loss accounting.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="count">The maximum allowed collection size.</param>
    private void RecordLoss(Guid tenant, long count)
    {
        _drops[tenant] = _drops.GetValueOrDefault(tenant) + count;
        WriteState("losses.json", _drops);
    }

    /// <summary>
    ///     Writes versioned spool metadata atomically without changing acknowledged segment
    ///     payloads.
    /// </summary>
    /// <param name="name">The recorded display name or fixed metric identity.</param>
    /// <param name="state">Versioned spool metadata to serialize atomically.</param>
    /// <typeparam name="T">Type of the operation result.</typeparam>
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

    /// <summary>
    ///     Measures the encoded envelope bytes charged against the spool budget.
    /// </summary>
    /// <param name="e">The normalized deployment observation.</param>
    /// <returns>The encoded envelope byte count charged against the spool budget.</returns>
    private static int Size(DeploymentLogEnvelope e)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            JsonSerializer.Serialize(e, TelemetryHttpTransport.Json)).Length + 128;
    }

    /// <summary>
    ///     Creates memory-only lookup metadata for one immutable stored envelope.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="e">The normalized deployment observation.</param>
    /// <returns>Non-payload lookup metadata for the immutable stored event.</returns>
    private static PendingIndex Index(string path, DeploymentLogEnvelope e)
    {
        return new PendingIndex(path, e.EventId, e.TenantId,
            e.Event.ProjectId, e.Event.DeploymentId, e.OrderId, e.StoredAt);
    }

    /// <summary>
    ///     Updates tenant and global buffered-byte counters under spool synchronization.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="bytes">The maximum encoded UTF-8 size of provider input.</param>
    private void AddBytes(Guid tenant, long bytes)
    {
        _bytes += bytes;
        _tenantBytes[tenant] = _tenantBytes.GetValueOrDefault(tenant) + bytes;
    }

    /// <summary>
    ///     Releases owned resources and completes the documented service lifetime.
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        _writerLock?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    ///     Queued redacted event and completion source awaiting durable acknowledgement.
    /// </summary>
    /// <param name="Tenant">The internal owner-account telemetry scope.</param>
    /// <param name="Event">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="Channel">The stable terminal channel used for routing.</param>
    /// <param name="Completion">Completion source fulfilled only after durable storage acknowledgement.</param>
    private sealed record PendingWrite(
        Guid Tenant,
        DeploymentDiagnosticEvent Event,
        string? Channel,
        TaskCompletionSource<DeploymentLogEnvelope> Completion);

    /// <summary>
    ///     Versioned immutable payload with its integrity checksum.
    /// </summary>
    /// <param name="Version">The private telemetry capability protocol version.</param>
    /// <param name="Payload">Serialized payload subject to bounded validation and redaction.</param>
    /// <param name="Checksum">Checksum used to verify persisted segment integrity.</param>
    private sealed record Segment(int Version, string Payload, string Checksum);

    /// <summary>
    ///     Non-payload metadata locating a stored event within the verified spool.
    /// </summary>
    /// <param name="Path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="EventId">The stable event identity used to deduplicate retries.</param>
    /// <param name="TenantId">The internal owner-account telemetry identity.</param>
    /// <param name="ProjectId">The project identity used for authorization or correlation.</param>
    /// <param name="DeploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="OrderId">
    ///     The durable ordering position; negative values represent unsaved live observations.
    /// </param>
    /// <param name="StoredAt">The UTC time at which the immutable envelope was durably stored.</param>
    private sealed record PendingIndex(
        string Path,
        Guid EventId,
        Guid TenantId,
        Guid ProjectId,
        Guid? DeploymentId,
        long OrderId,
        DateTimeOffset StoredAt);
}
