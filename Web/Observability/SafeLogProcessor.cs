using Infrastructure.Observability;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Web.Observability;

/// <summary>
///     Rechecks SDK attributes/body before exporters; scopes are snapshotted by SafeLoggerFactory.
/// </summary>
/// <param name="policy">Reviewed export or authorization policy.</param>
public sealed class SafeLogProcessor(PlatformTelemetryPolicy policy) : BaseProcessor<LogRecord>
{
    /// <summary>
    ///     Removes unapproved attributes and replaces the SDK log body with reviewed safe text before
    ///     export.
    /// </summary>
    /// <param name="record">SDK log record to review before export.</param>
    public override void OnEnd(LogRecord record)
    {
        var safe = policy.Log(record.Attributes, record.CategoryName, record.EventId.Id);
        record.Attributes = safe;
        record.Body = safe.Last(pair => pair.Key == "{OriginalFormat}").Value as string;
        record.FormattedMessage = safe.Message;
        record.Exception = null;
        record.TraceState = null;
        record.CategoryName = policy.Category(record.CategoryName);
        record.EventId = new EventId(record.EventId.Id);
    }
}