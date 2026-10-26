using Application.Abstractions.Diagnostics.Events;

namespace Web.Services;

/// <summary>
///     Owns bounded terminal buffering and replay deduplication for one renderer-owned project view.
/// </summary>
public sealed class ProjectTerminalReplayState
{
    /// <summary>Events waiting for terminal initialization.</summary>
    private readonly List<DeploymentTerminalLog> _pending = [];

    /// <summary>Durable identities rendered ahead of the acknowledged history cursor.</summary>
    private readonly HashSet<long> _rendered = [];

    /// <summary>The latest confirmed replay order; live-only events never advance it.</summary>
    public long LastOrderId { get; private set; }

    /// <summary>Whether bounded buffering or live deduplication has omitted output.</summary>
    public bool Overflow { get; private set; }

    /// <summary>
    ///     Buffers up to the existing 512 events while terminals are initializing.
    /// </summary>
    /// <param name="log">The source-aware redacted event.</param>
    public void Buffer(DeploymentTerminalLog log)
    {
        if (_pending.Count < 512) _pending.Add(log);
        else Overflow = true;
    }

    /// <summary>
    ///     Captures buffered events in stable order for asynchronous rendering.
    /// </summary>
    /// <returns>An independent ordered snapshot; it does not clear the buffer.</returns>
    public IReadOnlyList<DeploymentTerminalLog> PendingSnapshot()
    {
        return [.. _pending.OrderBy(item => item.OrderId)];
    }

    /// <summary>
    ///     Clears buffered events after replay or a status transition.
    /// </summary>
    public void ClearPending()
    {
        _pending.Clear();
    }

    /// <summary>
    ///     Applies the existing 2,000-identity bound, preserving replay and live-only acceptance behavior.
    /// </summary>
    /// <param name="orderId">The durable order, or a negative live-only identity.</param>
    /// <param name="isReplay">Whether an event was obtained from acknowledged saved history.</param>
    /// <returns>Whether this event should be rendered.</returns>
    public bool Accept(long orderId, bool isReplay)
    {
        if (orderId < 0)
            return true;

        if (orderId <= LastOrderId || _rendered.Contains(orderId))
            return false;

        if (_rendered.Count >= 2000 && !isReplay)
        {
            Overflow = true;
            return false;
        }

        if (_rendered.Count < 2000)
            _rendered.Add(orderId);

        return true;
    }

    /// <summary>
    ///     Advances only a confirmed replay cursor and retires identities behind it.
    /// </summary>
    /// <param name="orderId">The maximum order in the successfully rendered history page.</param>
    public void Confirm(long orderId)
    {
        LastOrderId = Math.Max(LastOrderId, orderId);
        _rendered.RemoveWhere(id => id <= LastOrderId);
    }

    /// <summary>
    ///     Clears the overflow indication once the page has displayed its existing guidance.
    /// </summary>
    public void AcknowledgeOverflow()
    {
        Overflow = false;
    }

    /// <summary>
    ///     Resets cursor, deduplication and buffering for a replacement deployment or route.
    /// </summary>
    public void Reset()
    {
        LastOrderId = 0;
        _rendered.Clear();
        _pending.Clear();
        Overflow = false;
    }
}
