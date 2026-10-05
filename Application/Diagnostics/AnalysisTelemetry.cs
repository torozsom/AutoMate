using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.Abstractions.Ai;
using Application.Ai;

namespace Application.Diagnostics;

/// <summary>Reviewed analysis steps; external text never becomes an operation name.</summary>
public enum AnalysisOperation
{
    /// <summary>Durable queue acquisition.</summary>
    Claim,

    /// <summary>One fenced processing attempt.</summary>
    Process,

    /// <summary>Bounded context acquisition.</summary>
    Context,

    /// <summary>Provider invocation and result validation.</summary>
    Provider,

    /// <summary>Lease heartbeat.</summary>
    Renew,

    /// <summary>Interruption cleanup.</summary>
    Release,

    /// <summary>Transactional result publication.</summary>
    Publish,

    /// <summary>Durable retry scheduling.</summary>
    Retry
}

/// <summary>Finite attempt outcomes, independent of provider error messages.</summary>
public enum AnalysisOutcome
{
    /// <summary>Operation committed successfully.</summary>
    Completed,

    /// <summary>Operation failed.</summary>
    Failed,

    /// <summary>Caller or ownership cancellation.</summary>
    Canceled,

    /// <summary>Policy or data did not permit execution.</summary>
    Skipped,

    /// <summary>No eligible work was available.</summary>
    Empty,

    /// <summary>Ownership or metadata no longer permitted publication.</summary>
    Discarded,

    /// <summary>A future attempt was committed.</summary>
    RetryScheduled
}

/// <summary>Payload-free analysis traces and finite-dimensional metrics shared without an SDK dependency.</summary>
public static class AnalysisTelemetry
{
    /// <summary>Stable analysis activity source.</summary>
    public static readonly ActivitySource Source = new("AutoMate.Analysis");

    /// <summary>Stable analysis meter.</summary>
    public static readonly Meter Meter = new("AutoMate.Analysis");

    /// <summary>Finished operations, including empty claims and discarded attempts.</summary>
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("automate.analysis.operations");

    /// <summary>Step latency measured using monotonic time.</summary>
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("automate.analysis.duration", "ms");

    /// <summary>Processing attempts currently executing in this host.</summary>
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("automate.analysis.active");

    /// <summary>Queue eligibility wait; delayed retry backoff is excluded.</summary>
    private static readonly Histogram<double> QueueWait =
        Meter.CreateHistogram<double>("automate.analysis.queue.wait", "ms");

    /// <summary>Validated provider-reported input token usage, never a local estimate.</summary>
    private static readonly Histogram<long> InputTokens =
        Meter.CreateHistogram<long>("automate.analysis.input.tokens", "{token}");

    /// <summary>Validated provider-reported output token usage.</summary>
    private static readonly Histogram<long> OutputTokens =
        Meter.CreateHistogram<long>("automate.analysis.output.tokens", "{token}");

    /// <summary>Begins a correlated operation without accepting provider names, text or arbitrary labels.</summary>
    public static Measurement Start(AnalysisOperation operation, Guid? deploymentId = null, Guid? analysisId = null,
        CancellationToken token = default)
    {
        return new Measurement(operation, deploymentId, analysisId, token);
    }

    /// <summary>Reports nonnegative queue wait only after a durable acquisition succeeds.</summary>
    public static void Claimed(DateTimeOffset eligibleAt, DateTimeOffset now)
    {
        QueueWait.Record(Math.Max(0, (now - eligibleAt).TotalMilliseconds));
    }

    /// <summary>Records known usage only after result validation; missing counts remain absent.</summary>
    public static void Usage(LlmAnalysisResponse response)
    {
        if (response.InputTokens is >= 0) InputTokens.Record(response.InputTokens.Value);
        if (response.OutputTokens is >= 0) OutputTokens.Record(response.OutputTokens.Value);
    }

    /// <summary>Preserves result and exception identity while completing a payload-free child measurement.</summary>
    public static async Task<T> RunAsync<T>(AnalysisOperation operation, Guid? deploymentId, Guid? analysisId,
        CancellationToken token, Func<Task<T>> action, Func<T, AnalysisOutcome>? outcome = null)
    {
        using var measurement = Start(operation, deploymentId, analysisId, token);
        try
        {
            var result = await action();
            measurement.Finish(outcome?.Invoke(result) ?? AnalysisOutcome.Completed);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            measurement.Finish(AnalysisOutcome.Canceled);
            throw;
        }
        catch (AnalysisProviderUnavailableException)
        {
            measurement.Finish(AnalysisOutcome.Skipped);
            throw;
        }
    }

    /// <summary>A single operation lifetime; disposal always balances active attempts and records exactly once.</summary>
    public sealed class Measurement : IDisposable
    {
        /// <summary>Optional sampled child span.</summary>
        private readonly Activity? _activity;

        /// <summary>Finite reviewed operation name.</summary>
        private readonly string _operation;

        /// <summary>Whether this scope contributes to active processing.</summary>
        private readonly bool _processing;

        /// <summary>Monotonic start timestamp.</summary>
        private readonly long _started = Stopwatch.GetTimestamp();

        /// <summary>Distinguishes cooperative interruption from default failure during disposal.</summary>
        private readonly CancellationToken _token;

        /// <summary>Guards duplicate disposal.</summary>
        private int _disposed;

        /// <summary>Default failure ensures exceptions cannot accidentally report success.</summary>
        private AnalysisOutcome _outcome = AnalysisOutcome.Failed;

        /// <summary>Starts only fixed-name operations with GUID correlation.</summary>
        internal Measurement(AnalysisOperation operation, Guid? deploymentId, Guid? analysisId, CancellationToken token)
        {
            _token = token;
            _operation = Enum.IsDefined(operation) ? operation.ToString().ToLowerInvariant() : "unknown";
            _processing = operation == AnalysisOperation.Process;
            _activity = Source.StartActivity("analysis." + _operation);
            if (deploymentId is { } deployment && deployment != Guid.Empty)
                _activity?.SetTag("deployment.id", deployment);
            if (analysisId is { } analysis && analysis != Guid.Empty)
                _activity?.SetTag("deployment.analysis.id", analysis);
            if (_processing) Active.Add(1);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_outcome == AnalysisOutcome.Failed && _token.IsCancellationRequested)
                _outcome = AnalysisOutcome.Canceled;
            var outcome = _outcome switch
            {
                AnalysisOutcome.RetryScheduled => "retry_scheduled",
                _ => _outcome.ToString().ToLowerInvariant()
            };
            var tags = new TagList { { "analysis.operation", _operation }, { "analysis.outcome", outcome } };
            Operations.Add(1, tags);
            Duration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, tags);
            if (_processing) Active.Add(-1);
            _activity?.SetTag("analysis.outcome", outcome);
            _activity?.SetStatus(_outcome == AnalysisOutcome.Failed ? ActivityStatusCode.Error :
                _outcome is AnalysisOutcome.Completed or AnalysisOutcome.RetryScheduled ? ActivityStatusCode.Ok :
                ActivityStatusCode.Unset);
            _activity?.Dispose();
        }

        /// <summary>Sets the final finite outcome; no untrusted enum value can create a label.</summary>
        public void Finish(AnalysisOutcome outcome)
        {
            _outcome = Enum.IsDefined(outcome) ? outcome : AnalysisOutcome.Failed;
        }
    }
}