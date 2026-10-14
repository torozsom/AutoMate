using System.Diagnostics;
using System.Net;
using System.Text;
using Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Web.Diagnostics;

/// <summary>
///     Console-only exception snapshots remain bounded and detached from the ordinary provider
///     boundary.
/// </summary>
public sealed class ConsoleExceptionDiagnosticsTests
{
    /// <summary>
    ///     Console includes useful nested failures and symbols while all ordinary providers remain
    ///     metadata-only.
    /// </summary>
    [Fact]
    public void Nested_failures_are_redacted_correlated_and_not_sent_to_other_providers()
    {
        using var writer = new StringWriter();
        var key = "plain-azure-key-123456789";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AiAnalysis:AzureOpenAi:ApiKey"] = key }).Build();
        using var capture = new CaptureProvider();
        using var inner = LoggerFactory.Create(logging => logging.AddProvider(capture));
        var policy = new PlatformTelemetryPolicy(new DiagnosticRedactor());
        using var factory = new SafeLoggerFactory(inner, policy,
            new ConsoleExceptionDiagnostics(new DiagnosticRedactor(), configuration, writer));
        var logger = factory.CreateLogger("Application.Orchestration.CloudDeploymentOrchestrator");
        var deployment = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var fields = new Dictionary<string, object?>
        {
            ["DeploymentId"] = deployment,
            ["AnalysisId"] = analysis,
            ["Cookie"] = "private-cookie"
        };
        using var activity = new Activity("console-test").Start();
        using (logger.BeginScope(fields))
        {
            fields["DeploymentId"] = Guid.NewGuid();
            logger.LogError(new InvalidOperationException("Provisioning failed " + key,
                    ThrownFailure("password=private-password https://private.invalid?token=private-query")),
                "Cloud deployment preparation failed: {FailureType}.", "InvalidOperationException");
        }

        var console = writer.ToString();
        Assert.Contains("Provisioning failed", console);
        Assert.Contains("HttpRequestException", console);
        Assert.Contains("ThrownFailure", console);
        Assert.Contains("HTTP status: 403", console);
        Assert.Contains(deployment.ToString(), console);
        Assert.Contains(analysis.ToString(), console);
        Assert.Contains(activity.TraceId.ToString(), console);
        Assert.DoesNotContain(key, console);
        Assert.DoesNotContain("private", console);
        Assert.DoesNotContain(".cs:", console);
        var ordinary = Assert.Single(capture.Records);
        Assert.Null(ordinary.Exception);
        Assert.DoesNotContain("Provisioning failed", ordinary.Message);
        Assert.Contains("403", console);
    }

    /// <summary>
    ///     UTF-8 records and nested/broad exceptions cannot create unbounded console output.
    /// </summary>
    [Fact]
    public void Oversized_and_deep_exceptions_remain_within_utf8_record_limit()
    {
        using var writer = new StringWriter();
        var diagnostics = new ConsoleExceptionDiagnostics(new DiagnosticRedactor(), writer: writer);
        var error = new Exception("deepest-marker");
        for (var index = 0; index < 10; index++)
            error = new AggregateException(new string('界', 6000), error, new Exception("sibling"));
        diagnostics.Write("AutoMate.Platform", LogLevel.Error, 0, new SafePlatformLog("safe", []),
            new LoggerExternalScopeProvider(), error);
        var text = writer.ToString();
        Assert.NotEmpty(text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= ConsoleExceptionDiagnostics.MaximumBytes);
        Assert.DoesNotContain("deepest-marker", text);
        Assert.DoesNotContain("Exception depth 4", text);
        Assert.Contains("truncated", text);
    }

    /// <summary>
    ///     Whole multiline response/header payloads and filesystem locations are omitted.
    /// </summary>
    [Fact]
    public void Provider_payloads_and_source_paths_are_suppressed()
    {
        using var writer = new StringWriter();
        var diagnostics = new ConsoleExceptionDiagnostics(new DiagnosticRedactor(), writer: writer);
        diagnostics.Write("AutoMate.Platform", LogLevel.Error, 0, new SafePlatformLog("safe", []),
            new LoggerExternalScopeProvider(), new HttpRequestException(
                "Request failed at C:\\private-root\\file.cs\nresponse body: private-json\nprivate-continuation"));
        Assert.DoesNotContain("private", writer.ToString());
        Assert.Contains("Request failed", writer.ToString());
    }

    /// <summary>
    ///     Writer failures and malicious exception getters cannot interrupt the original operation.
    /// </summary>
    [Fact]
    public void Diagnostics_failures_are_isolated_and_disabled_log_levels_produce_nothing()
    {
        var diagnostics = new ConsoleExceptionDiagnostics(new DiagnosticRedactor(), writer: new FailingWriter());
        diagnostics.Write("AutoMate.Platform", LogLevel.Error, 0, new SafePlatformLog("safe", []),
            new LoggerExternalScopeProvider(), new Exception("safe failure"));
        diagnostics.Write("AutoMate.Platform", LogLevel.Error, 0, new SafePlatformLog("safe", []),
            new LoggerExternalScopeProvider(), new BrokenException());
        using var writer = new StringWriter();
        using var inner = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Critical).AddConsole());
        using var factory = new SafeLoggerFactory(inner, new PlatformTelemetryPolicy(new DiagnosticRedactor()),
            new ConsoleExceptionDiagnostics(new DiagnosticRedactor(), writer: writer));
        factory.CreateLogger("AutoMate.Platform").LogError(new Exception("filtered"), "failure");
        Assert.Equal("", writer.ToString());
    }

    /// <summary>
    ///     Console-specific filtering remains effective even when another provider enables the same
    ///     event.
    /// </summary>
    [Fact]
    public void Console_filter_disables_diagnostics_while_ordinary_metadata_is_still_logged()
    {
        using var writer = new StringWriter();
        using var capture = new CaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(capture));
        services.Configure<LoggerFilterOptions>(settings =>
            settings.Rules.Add(new LoggerFilterRule("Console", null, LogLevel.None, null)));
        services.AddSingleton(provider => new ConsoleExceptionDiagnostics(new DiagnosticRedactor(), writer: writer,
            filters: provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>()));
        services.AddSafePlatformLogging();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Application.Orchestration.CloudDeploymentOrchestrator")
            .LogError(new Exception("should not reach console"), "Cloud deployment preparation failed: {FailureType}.",
                "Exception");
        Assert.Single(capture.Records);
        Assert.Equal("", writer.ToString());
    }

    /// <summary>
    ///     Root-level Unix paths are removed as well as nested and Windows paths.
    /// </summary>
    [Fact]
    public void Unix_root_paths_do_not_leak_from_exception_messages()
    {
        using var writer = new StringWriter();
        new ConsoleExceptionDiagnostics(new DiagnosticRedactor(), writer: writer)
            .Write("AutoMate.Platform", LogLevel.Error, 0, new SafePlatformLog("safe", []),
                new LoggerExternalScopeProvider(), new Exception("Cannot read /private-file.cs"));
        Assert.Contains("Cannot read", writer.ToString());
        Assert.DoesNotContain("private-file", writer.ToString());
    }

    /// <summary>
    ///     Shared DI registration installs the sink by default and resolves configuration for key
    ///     masking.
    /// </summary>
    [Fact]
    public void Composition_installs_console_diagnostics_without_replacing_normal_providers()
    {
        var services = new ServiceCollection();
        services.AddSafePlatformLogging();
        services.AddSafePlatformLogging();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ConsoleExceptionDiagnostics>());
        Assert.IsType<SafeLoggerFactory>(provider.GetRequiredService<ILoggerFactory>());
    }

    /// <summary>
    ///     Produces real compiled stack frames without depending on a live service.
    /// </summary>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <returns>The configured exception instance or response for this scenario.</returns>
    private static Exception ThrownFailure(string message)
    {
        try
        {
            throw new HttpRequestException(message, null, HttpStatusCode.Forbidden);
        }
        catch (HttpRequestException error)
        {
            return error;
        }
    }

    /// <summary>
    ///     Intentionally fails writes to verify diagnostic isolation.
    /// </summary>
    private sealed class FailingWriter : StringWriter
    {
        /// <summary>
        ///     Rejects write line because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="value">Optional synthetic input text; ignored by this fixture.</param>
        /// <exception cref="IOException">The fixture injects the configured storage or output failure.</exception>
        public override void WriteLine(string? value)
        {
            throw new IOException("console unavailable");
        }
    }

    /// <summary>
    ///     A hostile getter must not prevent business logging.
    /// </summary>
    private sealed class BrokenException : Exception
    {
        /// <summary>Throwing exception-message getter used to verify fail-safe diagnostics.</summary>
        /// <exception cref="InvalidOperationException">The fixture deliberately rejects reading its exception message.</exception>
        public override string Message => throw new InvalidOperationException("getter unavailable");
    }

    /// <summary>
    ///     Ordinary provider capture receives only detached approved state.
    /// </summary>
    private sealed class CaptureProvider : ILoggerProvider
    {
        /// <summary>Captured safe events.</summary>
        public List<Record> Records { get; } = [];

        /// <summary>
        ///     Creates a logger writing to the fixture capture provider.
        /// </summary>
        /// <param name="categoryName">Logger category reviewed before platform output; ignored by this fixture.</param>
        /// <returns>A logger attached to the fixture capture provider.</returns>
        public ILogger CreateLogger(string categoryName)
        {
            return new CaptureLogger(Records);
        }

        /// <summary>
        ///     Performs no dispose work in this fixture.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        ///     Records the formatter output and whether an exception crossed the boundary.
        /// </summary>
        /// <param name="records">Captured log records retained for assertions.</param>
        private sealed class CaptureLogger(List<Record> records) : ILogger
        {
            /// <summary>
            ///     Returns no scope handle because this fixture does not retain scopes.
            /// </summary>
            /// <param name="state">Synthetic readiness or structured logger state; ignored by this fixture.</param>
            /// <typeparam name="TState">Type of structured logger state.</typeparam>
            /// <returns>Null; this fixture retains no scopes.</returns>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
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
            /// <param name="logLevel">Log severity used by the underlying logger filter; ignored by this fixture.</param>
            /// <param name="eventId">The stable event identity used to deduplicate retries; ignored by this fixture.</param>
            /// <param name="state">Synthetic readiness or structured logger state.</param>
            /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
            /// <param name="formatter">Provider log formatter; untrusted rendered text is not exported.</param>
            /// <typeparam name="TState">Type of structured logger state.</typeparam>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                records.Add(new Record(formatter(state, exception), exception));
            }
        }
    }

    /// <summary>
    ///     Detached ordinary-provider assertion data.
    /// </summary>
    /// <param name="Message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <param name="Exception">Connection failure, or null for a normal disconnect.</param>
    private sealed record Record(string Message, Exception? Exception);
}
