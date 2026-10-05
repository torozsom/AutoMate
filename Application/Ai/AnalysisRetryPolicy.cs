namespace Application.Ai;

/// <summary>Safe transient signal carrying only a bounded numeric server delay, never an error body or inner exception.</summary>
public sealed class TransientAnalysisProviderException : Exception
{
    /// <summary>Creates a fixed safe error; large server delays remain above policy limits and cannot trigger early retries.</summary>
    public TransientAnalysisProviderException(int? retryAfterSeconds = null) : base(
        "AI provider temporarily unavailable.")
    {
        RetryAfterSeconds = retryAfterSeconds is null ? null : Math.Clamp(retryAfterSeconds.Value, 0, 86_400);
    }

    /// <summary>Optional server minimum delay; policy refuses values above its configured maximum.</summary>
    public int? RetryAfterSeconds { get; }
}

/// <summary>Bounds retries and exponential waits; durable scheduling and fresh consent checks belong to the worker.</summary>
public static class AnalysisRetryPolicy
{
    /// <summary>Returns a jittered delay or null on exhaustion/unsupported server delay; custom options are clamped.</summary>
    public static TimeSpan? Delay(AiAnalysisOptions options, int completedRetries, int? retryAfterSeconds)
    {
        var retries = Math.Clamp(options.MaximumProviderRetries, 0, 5);
        if (completedRetries < 0 || completedRetries >= retries) return null;
        var maximum = Math.Clamp(options.RetryMaximumDelaySeconds, 5, 3600);
        if (retryAfterSeconds > maximum) return null;
        var baseline = Math.Clamp(options.RetryBaseDelaySeconds, 1, maximum);
        var exponential = Math.Min(maximum, baseline * (1 << completedRetries));
        var minimum = Math.Max(exponential, Math.Max(0, retryAfterSeconds ?? 0));
        var delay = Math.Min(maximum, minimum + Random.Shared.NextDouble() * baseline / 4);
        return TimeSpan.FromSeconds(delay);
    }
}