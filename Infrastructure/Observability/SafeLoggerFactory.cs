using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability;

/// <summary>Sanitizes before any registered ILogger provider, including standard console/debug and OpenTelemetry.</summary>
public sealed class SafeLoggerFactory(
    ILoggerFactory inner,
    PlatformTelemetryPolicy policy,
    ConsoleExceptionDiagnostics? diagnostics = null) : ILoggerFactory
{
    /// <summary>Independent safe scope chain for console snapshots; no raw scope values are retained.</summary>
    private readonly LoggerExternalScopeProvider scopes = new();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        var category = policy.Category(categoryName);
        return new SafeLogger(inner.CreateLogger(category), policy, category, diagnostics, scopes);
    }

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        inner.AddProvider(provider);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        inner.Dispose();
    }

    /// <summary>Snapshots scopes and replaces raw state, exceptions and caller-provided formatters before logging.</summary>
    private sealed class SafeLogger(
        ILogger innerLogger,
        PlatformTelemetryPolicy policy,
        string category,
        ConsoleExceptionDiagnostics? diagnostics,
        IExternalScopeProvider scopes) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var fields = policy.Fields(state);
            var safe = new SafePlatformLog(
                string.Join(" ", fields.Select(pair => $"{pair.Key}={pair.Value}")), fields.ToArray());
            var innerScope = innerLogger.BeginScope(safe);
            return new Scope(innerScope, scopes.Push(safe));
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
        {
            return innerLogger.IsEnabled(logLevel);
        }

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var safe = policy.Log(state, category, eventId.Id, exception);
            innerLogger.Log(logLevel, new EventId(eventId.Id), safe, null, static (value, _) => value.Message);
            if (exception is not null) diagnostics?.Write(category, logLevel, eventId.Id, safe, scopes, exception);
        }
    }

    /// <summary>Closes both safe scope chains in reverse order.</summary>
    private sealed class Scope(IDisposable? innerScope, IDisposable consoleScope) : IDisposable
    {
        /// <inheritdoc />
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