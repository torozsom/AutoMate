using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.Diagnostics;
using OpenTelemetry;

namespace Infrastructure.Observability.OpenTelemetry;

/// <summary>
///     Filters only fast successful leaf operations after privacy sanitization; metrics stay complete.
/// </summary>
/// <param name="options">Validated retention rates and duration thresholds.</param>
public sealed class OperationalTraceFilter(OpenTelemetryOptions options) : BaseProcessor<Activity>
{
    /// <summary>Intentional volume reductions, separate from privacy omissions.</summary>
    private static readonly Counter<long> Filtered = AutoMateTelemetry.DeploymentMeter.CreateCounter<long>(
        "automate.platform.spans.filtered");

    /// <summary>
    ///     Retains failed, canceled, slow, unknown-outcome and non-target spans unchanged.
    /// </summary>
    /// <param name="activity">Completed sanitized activity.</param>
    public override void OnEnd(Activity activity)
    {
        if (!activity.Recorded || activity.Status == ActivityStatusCode.Error) return;
        var rate = 1d;
        var operation = "unknown";
        if (activity.Source.Name == "AutoMate.Deployments" && activity.OperationName == "deployment.signalr.send" &&
            activity.GetTagItem("deployment.outcome") as string == "completed" &&
            activity.Duration.TotalMilliseconds < options.SignalRSlowMilliseconds)
        {
            rate = options.SignalRSuccessSampleRate;
            operation = "signalr_send";
        }
        else if (activity.Source.Name == "AutoMate.Analysis" && activity.OperationName == "analysis.claim" &&
                 activity.GetTagItem("analysis.outcome") as string == "empty" &&
                 activity.Duration.TotalMilliseconds < options.ClaimSlowMilliseconds)
        {
            rate = options.EmptyClaimSampleRate;
            operation = "empty_claim";
        }

        if (Retain(activity.SpanId, rate)) return;
        activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        Filtered.Add(1, new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("reason", "fast_success"));
    }

    /// <summary>
    ///     Uses deterministic FNV-1a over span identity without mutable sampler state.
    /// </summary>
    /// <param name="id">Span identity.</param>
    /// <param name="rate">Retention fraction from zero through one.</param>
    /// <returns>Whether the span belongs to the retained fraction.</returns>
    internal static bool Retain(ActivitySpanId id, double rate)
    {
        if (rate >= 1) return true;
        if (rate <= 0) return false;
        Span<byte> bytes = stackalloc byte[8];
        id.CopyTo(bytes);
        var hash = 2166136261;
        foreach (var value in bytes) hash = unchecked((hash ^ value) * 16777619);
        return hash / 4294967296d < rate;
    }
}
