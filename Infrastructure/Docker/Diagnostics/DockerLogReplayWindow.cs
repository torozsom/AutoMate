using System.Globalization;
using Application.Abstractions.Diagnostics;

namespace Infrastructure.Docker;

/// <summary>Deduplicates bounded timestamp overlap by occurrence, preserving identical legitimate repeated messages.</summary>
internal sealed class DockerLogReplayWindow
{
    /// <summary>Delivered occurrence counts for a bounded set of timestamp/stream/message identities.</summary>
    private readonly Dictionary<string, int> _delivered = new();

    /// <summary>Occurrence counts in the current provider subscription.</summary>
    private readonly Dictionary<string, int> _observed = new();

    /// <summary>Insertion order used for bounded eviction.</summary>
    private readonly Queue<string> _order = new();

    /// <summary>Latest durably confirmed provider timestamp.</summary>
    internal DateTimeOffset? LastTimestamp { get; private set; }

    /// <summary>Whether checkpoint capacity forced loss of overlap identities.</summary>
    internal bool Evicted { get; private set; }

    /// <summary>Resets per-subscription counts while retaining durable overlap confirmations.</summary>
    internal void BeginSubscription()
    {
        _observed.Clear();
    }

    /// <summary>Restores recent confirmations from Loki/disk history rather than a PostgreSQL payload buffer.</summary>
    internal void Restore(IEnumerable<DeploymentTerminalLog> history, string containerId)
    {
        foreach (var log in history.Where(e => e.SourceInstanceId == containerId && e.SourceCursor is not null))
        {
            var slash = log.SourceCursor!.LastIndexOf('/');
            if (slash < 1 || !int.TryParse(log.SourceCursor.AsSpan(slash + 1), out var occurrence) || occurrence < 1 ||
                log.TimestampUtc is null || log.Stream is null) continue;
            Confirm(Key(log.SourceCursor[..slash], log.Stream.Value, log.Message), occurrence, log.TimestampUtc.Value);
        }
    }

    /// <summary>Returns a cursor and idempotent identity only for an occurrence not already durably confirmed.</summary>
    internal (string Key, int Occurrence, string Cursor)? Observe(string timestamp, DeploymentDiagnosticStream stream,
        string safeMessage)
    {
        var key = Key(timestamp, stream, safeMessage);
        if (!_observed.ContainsKey(key) && _observed.Count >= 4096)
        {
            _observed.Clear();
            Evicted = true;
        }

        var count = _observed.GetValueOrDefault(key) + 1;
        _observed[key] = count;
        return count <= _delivered.GetValueOrDefault(key)
            ? null
            : (key, count, $"{timestamp}/{count.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Advances only after durable acceptance, retaining a finite overlap identity set.</summary>
    internal void Confirm(string key, int occurrence, DateTimeOffset timestamp)
    {
        if (!_delivered.ContainsKey(key))
        {
            if (_order.Count >= 4096)
            {
                _delivered.Remove(_order.Dequeue());
                Evicted = true;
            }

            _order.Enqueue(key);
        }

        _delivered[key] = Math.Max(occurrence, _delivered.GetValueOrDefault(key));
        if (LastTimestamp is null || timestamp > LastTimestamp) LastTimestamp = timestamp;
    }

    /// <summary>Hashes already-redacted content; the digest is never a secret-bearing metric dimension.</summary>
    private static string Key(string timestamp, DeploymentDiagnosticStream stream, string message)
    {
        return $"{timestamp}/{stream}/{DockerDiagnosticNormalizer.Identity(message):N}";
    }
}