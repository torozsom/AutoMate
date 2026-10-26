using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability;

/// <summary>
///     Sanitizes before any registered ILogger provider, including standard console/debug and
///     OpenTelemetry.
/// </summary>
/// <param name="inner">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="policy">Shared allowlist for finite categories, messages and structured metadata.</param>
/// <param name="diagnostics">
///     Optional bounded redacted console exception sink, separate from exported telemetry.
/// </param>
public sealed class SafeLoggerFactory(
    ILoggerFactory inner,
    PlatformTelemetryPolicy policy,
    ConsoleExceptionDiagnostics? diagnostics = null) : ILoggerFactory
{
    /// <summary>Independent safe scope chain for console snapshots; no raw scope values are retained.</summary>
    private readonly LoggerExternalScopeProvider scopes = new();

    /// <summary>
    ///     Wraps a category logger so every record crosses the shared platform safety policy.
    /// </summary>
    /// <param name="categoryName">Logger category reviewed before platform output.</param>
    /// <returns>A logger that filters records through the shared safety boundary.</returns>
    public ILogger CreateLogger(string categoryName)
    {
        var category = policy.Category(categoryName);
        return new SafeLogger(inner.CreateLogger(category), policy, category, diagnostics, scopes);
    }

    /// <summary>
    ///     Registers an output provider behind the shared reviewed-record boundary.
    /// </summary>
    /// <param name="provider">
    ///     Receives reviewed operational events without diagnostic payloads or credentials.
    /// </param>
    public void AddProvider(ILoggerProvider provider)
    {
        inner.AddProvider(provider);
    }

    /// <summary>
    ///     Releases owned resources and completes the documented service lifetime.
    /// </summary>
    public void Dispose()
    {
        inner.Dispose();
    }

    /// <summary>
    ///     Snapshots scopes and replaces raw state, exceptions and caller-provided formatters
    ///     before logging.
    /// </summary>
    /// <param name="innerLogger">
    ///     Receives reviewed operational events without diagnostic payloads or credentials.
    /// </param>
    /// <param name="policy">Shared allowlist for finite categories, messages and structured metadata.</param>
    /// <param name="category">Reviewed logger category or provider diagnostic category.</param>
    /// <param name="diagnostics">
    ///     Optional bounded redacted console exception sink, separate from exported telemetry.
    /// </param>
    /// <param name="scopes">Provides logger scopes reviewed before export.</param>
    private sealed class SafeLogger(
        ILogger innerLogger,
        PlatformTelemetryPolicy policy,
        string category,
        ConsoleExceptionDiagnostics? diagnostics,
        IExternalScopeProvider scopes) : ILogger
    {
        /// <summary>
        ///     Creates a detached safe correlation scope from allowlisted structured values.
        /// </summary>
        /// <param name="state">Untrusted structured log state reviewed before output.</param>
        /// <typeparam name="TState">
        ///     Type of the structured state reviewed or persisted by this operation.
        /// </typeparam>
        /// <returns>The owned safe correlation scope, or null when no reviewed scope can be created.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var fields = policy.Fields(state);
            var safe = new SafePlatformLog(
                string.Join(" ", fields.Select(pair => $"{pair.Key}={pair.Value}")), [.. fields]);
            var innerScope = innerLogger.BeginScope(safe);
            return new Scope(innerScope, scopes.Push(safe));
        }

        /// <summary>
        ///     Checks the underlying logger severity filter.
        /// </summary>
        /// <param name="logLevel">Log severity used by the underlying logger filter.</param>
        /// <returns>Whether the underlying logger accepts the requested severity.</returns>
        public bool IsEnabled(LogLevel logLevel)
        {
            return innerLogger.IsEnabled(logLevel);
        }

        /// <summary>
        ///     Reviews untrusted event state, formatter and exception metadata before provider
        ///     delivery.
        /// </summary>
        /// <param name="logLevel">Log severity used by the underlying logger filter.</param>
        /// <param name="eventId">The stable event identity used to deduplicate retries.</param>
        /// <param name="state">Untrusted structured log state reviewed before output.</param>
        /// <param name="exception">Optional original exception reviewed before safe platform logging.</param>
        /// <param name="formatter">Provider log formatter; untrusted rendered text is not exported.</param>
        /// <typeparam name="TState">
        ///     Type of the structured state reviewed or persisted by this operation.
        /// </typeparam>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var safe = policy.Log(state, category, eventId.Id, exception);
            innerLogger.Log(logLevel, new EventId(eventId.Id), safe, null, static (value, _) => value.Message);
            if (exception is not null) diagnostics?.Write(category, logLevel, eventId.Id, safe, scopes, exception);
        }
    }

    /// <summary>
    ///     Closes both safe scope chains in reverse order.
    /// </summary>
    /// <param name="innerScope">Underlying logger correlation scope, if created.</param>
    /// <param name="consoleScope">Owned console-diagnostic correlation scope.</param>
    private sealed class Scope(IDisposable? innerScope, IDisposable consoleScope) : IDisposable
    {
        /// <summary>
        ///     Releases owned resources and completes the documented service lifetime.
        /// </summary>
        public void Dispose()
        {
            try
            {
                consoleScope.Dispose();
            }
            finally
            {
                innerScope?.Dispose();
            }
        }
    }
}
