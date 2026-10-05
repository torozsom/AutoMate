using System.Security.Claims;
using Infrastructure.Diagnostics;
using Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Web.Observability;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies safe request audit boundaries for successful, rejected and failed operations.</summary>
public sealed class RequestAuditTests
{
    /// <summary>Every response preserves method, status, duration, actor and module without request payloads.</summary>
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Request_audit_records_response_and_correlation_without_credentials(int status)
    {
        using var capture = new Capture();
        using var inner = LoggerFactory.Create(logging => logging.AddProvider(capture));
        using var factory = new SafeLoggerFactory(inner, new PlatformTelemetryPolicy(new DiagnosticRedactor()));
        var actor = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                new Claim(ClaimTypes.Email, "private-email")], "Cookies"))
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/deployments/private-path";
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Request.Headers.Authorization = "Bearer private-header";
        var middleware = new RequestAuditMiddleware(http =>
        {
            http.Response.StatusCode = status;
            return Task.CompletedTask;
        }, factory.CreateLogger<RequestAuditMiddleware>());
        await middleware.InvokeAsync(context);
        Assert.Equal(2, capture.Messages.Count);
        var text = string.Join('\n', capture.Messages);
        Assert.Contains("Web.Observability.RequestAuditMiddleware", text);
        Assert.Contains("method POST", text);
        Assert.Contains("area Deployments", text);
        Assert.Contains($"status {status}", text);
        Assert.Contains(actor.ToString(), text);
        Assert.Contains("RequestId=", text);
        Assert.DoesNotContain("private", text);
    }

    /// <summary>Failures retain exception identity and class while withholding the exception body.</summary>
    [Fact]
    public async Task Request_audit_rethrows_failure_without_logging_provider_body()
    {
        using var capture = new Capture();
        using var inner = LoggerFactory.Create(logging => logging.AddProvider(capture));
        using var factory = new SafeLoggerFactory(inner, new PlatformTelemetryPolicy(new DiagnosticRedactor()));
        var failure = new IOException("private-provider-body");
        var middleware = new RequestAuditMiddleware(_ => Task.FromException(failure),
            factory.CreateLogger<RequestAuditMiddleware>());
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(new DefaultHttpContext())));
        Assert.Contains("IOException", capture.Messages.Last());
        Assert.DoesNotContain("private", string.Join('\n', capture.Messages));
    }

    /// <summary>Captures the same safe message, category and printable scopes consumed by ordinary console providers.</summary>
    private sealed class Capture : ILoggerProvider, ISupportExternalScope
    {
        /// <summary>Factory-owned scope stack.</summary>
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        /// <summary>Captured audit records.</summary>
        public List<string> Messages { get; } = [];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        /// <inheritdoc />
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        /// <inheritdoc />
        public void Dispose() { }

        /// <summary>Observes provider-facing state without invoking raw application formatters.</summary>
        private sealed class Logger(Capture owner, string category) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Assert.Null(exception);
                var scopes = new List<string>();
                owner._scopes.ForEachScope((scope, list) => list.Add(scope?.ToString() ?? ""), scopes);
                owner.Messages.Add($"{category} {string.Join(' ', scopes)} {formatter(state, exception)}");
            }
        }
    }
}
