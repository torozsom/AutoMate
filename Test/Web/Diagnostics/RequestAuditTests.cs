using System.Security.Claims;
using Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Web.Observability;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Web.Diagnostics;

/// <summary>
///     Verifies safe request audit boundaries for successful, rejected and failed operations.
/// </summary>
public sealed class RequestAuditTests
{
    /// <summary>
    ///     Request methods become finite labels before reaching any logger, including when downstream
    ///     middleware mutates the request method.
    /// </summary>
    /// <param name="method">Standard, custom or hostile method supplied by the request.</param>
    /// <param name="expected">Fixed label expected in both audit records.</param>
    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("POST", "POST")]
    [InlineData("PUT", "PUT")]
    [InlineData("PATCH", "PATCH")]
    [InlineData("DELETE", "DELETE")]
    [InlineData("HEAD", "HEAD")]
    [InlineData("OPTIONS", "OPTIONS")]
    [InlineData("CONNECT", "CONNECT")]
    [InlineData("TRACE", "TRACE")]
    [InlineData("CUSTOM", "OTHER")]
    [InlineData("get", "OTHER")]
    [InlineData("GET\r\nforged-entry", "OTHER")]
    [InlineData("<script>private</script>", "OTHER")]
    public async Task Request_methods_are_safe_without_a_sanitizing_logger(string method, string expected)
    {
        var capture = new Capture();
        using var factory =
            LoggerFactory.Create(logging => logging.Services.AddSingleton<ILoggerProvider>(_ => capture));
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        var middleware = new RequestAuditMiddleware(http =>
        {
            http.Request.Method = "private-mutated\r\nforged-entry";
            return Task.CompletedTask;
        }, factory.CreateLogger<RequestAuditMiddleware>());

        await middleware.InvokeAsync(context);

        Assert.Equal(2, capture.Messages.Count);
        foreach (var text in capture.Messages)
        {
            Assert.Contains($"method {expected},", text);
            Assert.DoesNotContain('\r', text);
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain("private", text);
            Assert.DoesNotContain("forged-entry", text);
        }
    }

    /// <summary>
    ///     Every response preserves method, status, duration, actor and module without request
    ///     payloads.
    /// </summary>
    /// <param name="status">Synthetic HTTP response status.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Request_audit_records_response_and_correlation_without_credentials(int status)
    {
        var capture = new Capture();
        using var inner = LoggerFactory.Create(logging => logging.Services.AddSingleton<ILoggerProvider>(_ => capture));
        using var factory = new SafeLoggerFactory(inner, new PlatformTelemetryPolicy(new DiagnosticRedactor()));
        var actor = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                new Claim(ClaimTypes.Email, "private-email")
            ], "Cookies"))
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

    /// <summary>
    ///     Failures retain exception identity and class while withholding the exception body.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Request_audit_rethrows_failure_without_logging_provider_body()
    {
        var capture = new Capture();
        using var inner = LoggerFactory.Create(logging => logging.Services.AddSingleton<ILoggerProvider>(_ => capture));
        using var factory = new SafeLoggerFactory(inner, new PlatformTelemetryPolicy(new DiagnosticRedactor()));
        var failure = new IOException("private-provider-body");
        var middleware = new RequestAuditMiddleware(_ => Task.FromException(failure),
            factory.CreateLogger<RequestAuditMiddleware>());

        Assert.Same(failure,
            await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(new DefaultHttpContext())));
        Assert.Contains("IOException", capture.Messages.Last());
        Assert.DoesNotContain("private", string.Join('\n', capture.Messages));
    }

    /// <summary>
    ///     Captures the same safe message, category and printable scopes consumed by ordinary console
    ///     providers.
    /// </summary>
    private sealed class Capture : ILoggerProvider, ISupportExternalScope
    {
        /// <summary>Factory-owned scope stack.</summary>
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        /// <summary>Captured audit records.</summary>
        public List<string> Messages { get; } = [];

        /// <summary>
        ///     Creates a logger writing to the fixture capture provider.
        /// </summary>
        /// <param name="categoryName">Logger category reviewed before platform output.</param>
        /// <returns>A logger attached to the fixture capture provider.</returns>
        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(this, categoryName);
        }

        /// <summary>
        ///     Performs no dispose work in this fixture.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        ///     Stores the external scope provider used by subsequent fixture logging.
        /// </summary>
        /// <param name="scopeProvider">Scope provider used to capture logger correlation.</param>
        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopes = scopeProvider;
        }

        /// <summary>
        ///     Observes provider-facing state without invoking raw application formatters.
        /// </summary>
        /// <param name="owner">Capture provider or owner name used by the fixture.</param>
        /// <param name="category">Reviewed logger category or provider diagnostic category.</param>
        private sealed class Logger(Capture owner, string category) : ILogger
        {
            /// <summary>
            ///     Pushes the supplied state into the fixture scope provider.
            /// </summary>
            /// <param name="state">Synthetic readiness or structured logger state.</param>
            /// <typeparam name="TState">Type of structured logger state.</typeparam>
            /// <returns>A disposable scope from the fixture external scope provider.</returns>
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                return owner._scopes.Push(state);
            }

            /// <summary>
            ///     Enables every log level so the fixture can observe all emitted records.
            /// </summary>
            /// <param name="logLevel">Log severity used by the underlying logger filter; ignored by this fixture.</param>
            /// <returns>True for every call in this fixture.</returns>
            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            /// <summary>
            ///     Captures or checks the logger call using the fixture assertions and supplied formatter.
            /// </summary>
            /// <param name="level">Reviewed log severity used to select safe output; ignored by this fixture.</param>
            /// <param name="eventId">The stable event identity used to deduplicate retries; ignored by this fixture.</param>
            /// <param name="state">Synthetic readiness or structured logger state.</param>
            /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
            /// <param name="formatter">Provider log formatter; untrusted rendered text is not exported.</param>
            /// <typeparam name="TState">Type of structured logger state.</typeparam>
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
