using System.Diagnostics.Metrics;
using Infrastructure.Diagnostics.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics.Metrics;

/// <summary>
///     Refreshes private-host spool and disk gauges off the exporter callback path.
/// </summary>
public sealed class TelemetrySpoolObserver : BackgroundService
{
    /// <summary>Filesystem probe executed only by the background refresh.</summary>
    private readonly Func<string, long> _availableBytes;

    /// <summary>Refresh and snapshot time source.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Owned instruments disappear when the host is disposed.</summary>
    private readonly Meter _meter = new("AutoMate.TelemetryStorage");

    /// <summary>Verified persistent volume configuration.</summary>
    private readonly IOptions<DiskSpoolOptions> _options;

    /// <summary>Authoritative single-writer indexes.</summary>
    private readonly DiskTelemetrySpool _spool;

    /// <summary>Last successful disk observation; absence is not zero capacity.</summary>
    private DiskSnapshot? _disk;

    /// <summary>Last successful index snapshot; null means not yet available.</summary>
    private TelemetrySpoolSnapshot? _snapshot;

    /// <summary>
    ///     Creates a host-owned observer with no filesystem work in measurement callbacks.
    /// </summary>
    /// <param name="spool">Authoritative spool indexes.</param>
    /// <param name="options">Persistent directory configuration.</param>
    public TelemetrySpoolObserver(DiskTelemetrySpool spool, IOptions<DiskSpoolOptions> options)
        : this(spool, options, TimeProvider.System, ReadAvailableBytes)
    {
    }

    /// <summary>
    ///     Supplies controlled time and filesystem availability for refresh/failure verification.
    /// </summary>
    /// <param name="spool">Authoritative spool indexes.</param>
    /// <param name="options">Persistent directory configuration.</param>
    /// <param name="clock">Refresh clock.</param>
    /// <param name="availableBytes">Owned filesystem probe.</param>
    internal TelemetrySpoolObserver(DiskTelemetrySpool spool, IOptions<DiskSpoolOptions> options,
        TimeProvider clock, Func<string, long> availableBytes)
    {
        _spool = spool;
        _options = options;
        _clock = clock;
        _availableBytes = availableBytes;
    }

    /// <summary>
    ///     Registers memory-only callbacks and refreshes state every five seconds, disk every thirty seconds.
    /// </summary>
    /// <param name="stoppingToken">Cancels refreshes before the spool is stopped.</param>
    /// <returns>The supervised observer lifetime.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Gauge("telemetry.spool.pending.events", s => s.PendingEvents);
        Gauge("telemetry.spool.current.bytes", s => s.BufferedBytes);
        Gauge("telemetry.spool.pending.segments", s => s.Segments);
        Gauge("telemetry.spool.oldest_pending.seconds", s => s.OldestPendingSeconds);
        Gauge("telemetry.spool.accepting", s => s.Accepting ? 1 : 0);
        Gauge("telemetry.spool.snapshot.age.seconds",
            s => Math.Max(0, (_clock.GetUtcNow() - s.ObservedAt).TotalSeconds));
        _meter.CreateObservableGauge("telemetry.spool.disk.available.bytes", () =>
            Volatile.Read(ref _disk) is { } d
                ? [new Measurement<long>(d.AvailableBytes)]
                : Array.Empty<Measurement<long>>());
        _meter.CreateObservableGauge("telemetry.spool.disk.snapshot.age.seconds", () =>
            Volatile.Read(ref _disk) is { } d
                ? [new Measurement<double>(Math.Max(0, (_clock.GetUtcNow() - d.ObservedAt).TotalSeconds))]
                : Array.Empty<Measurement<double>>());
        var nextDisk = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _clock);
        do
        {
            try
            {
                Volatile.Write(ref _snapshot,
                    await _spool.SnapshotAsync(stoppingToken) with { ObservedAt = _clock.GetUtcNow() });
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested &&
                                       ex is IOException or ObjectDisposedException)
            {
                // Keep the last snapshot and let its age expose the failed refresh.
            }

            if (_clock.GetUtcNow() >= nextDisk)
            {
                nextDisk = _clock.GetUtcNow().AddSeconds(30);
                try
                {
                    Volatile.Write(ref _disk,
                        new DiskSnapshot(_availableBytes(_options.Value.Directory), _clock.GetUtcNow()));
                }
                catch (Exception ex) when
                    (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // A failed disk probe retains its previous value and timestamp; no zero is invented.
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    ///     Resolves the longest matching mount path, including dedicated Linux spool volumes.
    /// </summary>
    /// <param name="directory">Verified spool directory.</param>
    /// <returns>Available bytes on the containing filesystem.</returns>
    private static long ReadAvailableBytes(string directory)
    {
        var path = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return DriveInfo.GetDrives().Where(d => d.IsReady &&
                                                (path.Equals(d.RootDirectory.FullName, comparison) ||
                                                 path.StartsWith(
                                                     Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName) +
                                                     Path.DirectorySeparatorChar, comparison)))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).First().AvailableFreeSpace;
    }

    /// <summary>
    ///     Creates a callback which reads only an immutable cached snapshot.
    /// </summary>
    /// <param name="name">Reviewed metric name.</param>
    /// <param name="value">Numeric projection from the cached state.</param>
    private void Gauge(string name, Func<TelemetrySpoolSnapshot, double> value)
    {
        _meter.CreateObservableGauge(name, () => Volatile.Read(ref _snapshot) is { } s
            ? [new Measurement<double>(value(s))]
            : Array.Empty<Measurement<double>>());
    }

    /// <summary>
    ///     Releases owned instruments after hosted refresh work has stopped.
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        _meter.Dispose();
    }

    /// <summary>
    ///     Immutable successful filesystem-capacity observation.
    /// </summary>
    /// <param name="AvailableBytes">Free bytes on the actual containing volume.</param>
    /// <param name="ObservedAt">Time of the successful probe.</param>
    private sealed record DiskSnapshot(long AvailableBytes, DateTimeOffset ObservedAt);
}
