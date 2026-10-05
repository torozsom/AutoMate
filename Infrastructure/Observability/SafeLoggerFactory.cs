using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability;

/// <summary>Sanitizes before any registered ILogger provider, including standard console/debug and OpenTelemetry.</summary>
public sealed class SafeLoggerFactory(ILoggerFactory inner, PlatformTelemetryPolicy policy) : ILoggerFactory
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        var category = policy.Category(categoryName);
        return new SafeLogger(inner.CreateLogger(category), policy, category);
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
    private sealed class SafeLogger(ILogger innerLogger, PlatformTelemetryPolicy policy, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var fields = policy.Fields(state);
            return innerLogger.BeginScope(new SafePlatformLog(
                string.Join(" ", fields.Select(pair => $"{pair.Key}={pair.Value}")), fields.ToArray()));
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
        }
    }
}