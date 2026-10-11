using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Auth;
using Application.Diagnostics;
using Application.Orchestration;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Diagnostics;
using Infrastructure.GitHub;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Web.Configs;
using Web.Routes.Endpoints.Auth;
using Xunit;

namespace Web.Tests;

/// <summary>Checks real SDK exports and framework authorization responses without contacting a collector.</summary>
public sealed class OperationalLoggingTests
{
    /// <summary>Fixed events retain GUID scopes and ambient trace correlation; exporter failure does not escape.</summary>
    [Fact]
    public void Audit_export_captures_scopes_trace_and_finite_event_properties()
    {
        using var exporter = new SnapshotExporter();
        using var factory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        }));
        var logger = factory.CreateLogger("fixture");
        var deployment = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        using var activity = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start();
        using (OperationalLog.BeginCorrelation(logger, deployment, analysis))
        {
            OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.InvalidResult);
        }

        var record = Assert.Single(exporter.Records);
        Assert.Equal("Analysis", record.Attributes["Operation"]);
        Assert.Equal("InvalidResult", record.Attributes["Outcome"]);
        Assert.Equal(deployment, record.Scopes["DeploymentId"]);
        Assert.Equal(analysis, record.Scopes["AnalysisId"]);
        Assert.Equal(activity.TraceId, record.TraceId);
        Assert.Null(record.Exception);
        Assert.Equal(1003, record.EventId.Id);
        exporter.Fail = true;
        OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Completed);
        Assert.Equal(2, exporter.Records.Count);
        Assert.Empty(exporter.Records.Last().Scopes);
    }

    /// <summary>Challenges/forbids retain the default framework response and never export principal credentials.</summary>
    [Theory]
    [InlineData("challenge", 401, "Challenged")]
    [InlineData("forbid", 403, "Denied")]
    [InlineData("success", 200, null)]
    public async Task Authorization_audit_preserves_framework_results(string result, int status, string? outcome)
    {
        using var exporter = new SnapshotExporter();
        var authentication = DispatchProxy.Create<IAuthenticationService, PortProxy>();
        ((PortProxy)authentication).Call = (method, args) =>
        {
            ((HttpContext)args![0]!).Response.StatusCode = method!.Name == "ChallengeAsync" ? 401 : 403;
            return Task.CompletedTask;
        };
        await using var services = new ServiceCollection().AddSingleton(authentication)
            .AddLogging(logging => logging.AddOpenTelemetry(options =>
                options.AddProcessor(new SimpleLogRecordExportProcessor(exporter)))).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Request.Headers.Authorization = "Bearer private-credential";
        var handler =
            new SecurityAuditResultHandler(services.GetRequiredService<ILogger<SecurityAuditResultHandler>>());
        var nextCalled = false;
        await handler.HandleAsync(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }, context,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(), result switch
            {
                "challenge" => PolicyAuthorizationResult.Challenge(),
                "forbid" => PolicyAuthorizationResult.Forbid(),
                _ => PolicyAuthorizationResult.Success()
            });
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(result == "success", nextCalled);
        if (outcome is null)
        {
            Assert.Empty(exporter.Records);
        }
        else
        {
            var record = Assert.Single(exporter.Records);
            Assert.Equal(outcome, record.Attributes["Outcome"]);
            Assert.DoesNotContain("private", record.Message!);
            Assert.Null(record.Exception);
            Assert.Empty(record.Scopes);
        }
    }

    /// <summary>A real dispatcher failure exports only safe correlation, never storage exception or terminal payload.</summary>
    [Fact]
    public async Task Diagnostic_failure_export_omits_raw_exception_and_payload()
    {
        using var exporter = new SnapshotExporter();
        var store = DispatchProxy.Create<IDeploymentDiagnosticStore, PortProxy>();
        ((PortProxy)store).Call = (_, _) =>
            Task.FromException<long>(new InvalidOperationException("password=private-storage-body"));
        var live = DispatchProxy.Create<ILogStreamer, PortProxy>();
        ((PortProxy)live).Call = (_, _) => Task.CompletedTask;
        await using var services = new ServiceCollection().AddSingleton(store).AddSingleton(live)
            .AddSingleton(Options.Create(new TelemetryStorageOptions
                { Backend = "LokiMimir", DeliveryMode = "DiskGateway" }))
            .AddLogging(logging => logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
                options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
            })).BuildServiceProvider();
        var factory = services.GetRequiredService<ILoggerFactory>();
        var publisher = new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(new DeploymentDiagnosticOptions()),
            factory.CreateLogger<DeploymentDiagnosticPublisher>(), services.GetRequiredService<IServiceScopeFactory>());
        using var worker = new DeploymentDiagnosticDispatcher(publisher, live,
            services.GetRequiredService<IServiceScopeFactory>(),
            factory.CreateLogger<DeploymentDiagnosticDispatcher>());
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await publisher.PublishAsync(new DeploymentDiagnosticEvent(project, deployment,
                DeploymentDiagnosticSource.DockerCompose,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
                "password=private-terminal-body", new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build)));
            await exporter.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var record = Assert.Single(exporter.Records, item => item.Level == LogLevel.Warning);
            Assert.Null(record.Exception);
            Assert.DoesNotContain("private", record.Message!);
            Assert.Equal(project, record.Scopes["ProjectId"]);
            Assert.Equal(deployment, record.Scopes["DeploymentId"]);
            Assert.Equal("InvalidOperationException", record.Attributes["FailureType"]);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Successful storage/delivery never copies terminal payloads into platform logs, even in legacy modes.</summary>
    [Theory]
    [InlineData("DiskGateway")]
    [InlineData("PostgresOutbox")]
    public async Task Diagnostic_success_export_omits_payload_in_every_delivery_mode(string deliveryMode)
    {
        using var exporter = new SnapshotExporter();
        var persisted =
            new TaskCompletionSource<DeploymentDiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered =
            new TaskCompletionSource<DeploymentTerminalLog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = DispatchProxy.Create<IDeploymentDiagnosticStore, PortProxy>();
        ((PortProxy)store).Call = (_, args) =>
        {
            persisted.TrySetResult((DeploymentDiagnosticEvent)args![0]!);
            return Task.FromResult(1L);
        };
        var live = DispatchProxy.Create<ILogStreamer, PortProxy>();
        ((PortProxy)live).Call = (_, args) =>
        {
            delivered.TrySetResult((DeploymentTerminalLog)args![0]!);
            return Task.CompletedTask;
        };
        await using var services = new ServiceCollection().AddSingleton(store).AddSingleton(live)
            .AddSingleton(Options.Create(new TelemetryStorageOptions { DeliveryMode = deliveryMode }))
            .AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddOpenTelemetry(options =>
                {
                    options.IncludeScopes = true;
                    options.IncludeFormattedMessage = true;
                    options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
                });
            }).BuildServiceProvider();
        var factory = services.GetRequiredService<ILoggerFactory>();
        var publisher = new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(new DeploymentDiagnosticOptions()), factory.CreateLogger<DeploymentDiagnosticPublisher>(),
            services.GetRequiredService<IServiceScopeFactory>());
        using var worker = new DeploymentDiagnosticDispatcher(publisher, live,
            services.GetRequiredService<IServiceScopeFactory>(),
            factory.CreateLogger<DeploymentDiagnosticDispatcher>());
        var observation = new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(),
            DeploymentDiagnosticSource.DockerCompose, DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            "private-terminal-body password=private-value",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await publisher.PublishAsync(observation);
            var saved = await persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var terminal = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("private-terminal-body password=[REDACTED]", saved.Message);
            Assert.Equal(saved.Message, terminal.Message);
            Assert.Equal(observation.ProjectId, terminal.ProjectId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var exported = JsonSerializer.Serialize(exporter.Records);
        Assert.DoesNotContain("private-terminal-body", exported);
        Assert.DoesNotContain("private-value", exported);
        Assert.DoesNotContain("DiagnosticMessage", exported);
        Assert.Contains(exporter.Records, record => record.Message == "Deployment diagnostic dispatcher started.");
    }

    /// <summary>Real form login keeps redirects/cookie sign-in while audit records omit credential and error fields.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_login_audit_preserves_response_without_credential_properties(bool accepted)
    {
        using var exporter = new SnapshotExporter();
        var auth = DispatchProxy.Create<IAuthService, PortProxy>();
        ((PortProxy)auth).Call = (method, args) =>
        {
            Assert.Equal("LoginAsync", method!.Name);
            Assert.Equal("private-email@example.invalid", args![0]);
            Assert.Equal("private-password", args[1]);
            return Task.FromResult<(LocalUser?, string?)>(accepted
                ? (
                    new LocalUser
                        { Id = Guid.NewGuid(), Username = "private-user", Email = "private-email@example.invalid" },
                    null)
                : (null, "private-auth-error"));
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        });
        builder.Services.AddSingleton(auth);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        new LoginEndpoint().Map(app);
        app.MapGet("/fixture-token", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!));
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler
                { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
            { BaseAddress = new Uri(app.Urls.Single()) };
        var token = await client.GetStringAsync("/fixture-token");
        using var response = await client.PostAsync("/api/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["email"] = "private-email@example.invalid",
                ["password"] = "private-password",
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(accepted ? "/" : "/login?error=private-auth-error", response.Headers.Location!.OriginalString);
        Assert.Equal(accepted, response.Headers.Contains("Set-Cookie"));
        var record = Assert.Single(exporter.Records,
            entry => entry.Attributes.GetValueOrDefault("Operation") as string == "Authentication");
        Assert.Equal(accepted ? "Completed" : "Denied", record.Attributes["Outcome"]);
        Assert.Null(record.Exception);
        Assert.DoesNotContain("private", record.Message!);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(record.Attributes));
    }

    /// <summary>A failing UI subscriber neither stops status delivery nor exports circuit exception text.</summary>
    [Fact]
    public void Status_subscriber_failure_retains_delivery_without_exception_payload()
    {
        using var exporter = new SnapshotExporter();
        using var factory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        }));
        var notifier = new DeploymentStatusNotifier(factory.CreateLogger<DeploymentStatusNotifier>());
        var projectId = Guid.NewGuid();
        var delivered = false;
        notifier.OnStatusChanged += (_, _) => throw new InvalidOperationException("private-circuit-token");
        notifier.OnStatusChanged += (id, status) =>
            delivered = id == projectId && status == DeploymentStatus.Failed;
        notifier.NotifyStatusChanged(projectId, DeploymentStatus.Failed);
        Assert.True(delivered);
        var warning = Assert.Single(exporter.Records);
        Assert.Equal(projectId, warning.Attributes["ProjectId"]);
        Assert.Equal(nameof(InvalidOperationException), warning.Attributes["FailureType"]);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain("private", warning.Message!);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(warning.Attributes));
    }

    /// <summary>Cache outages retain API fallback and cancellation behavior without exporting sensitive failures.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Repository_cache_failure_exports_safe_properties(bool write, bool canceled)
    {
        using var exporter = new SnapshotExporter();
        using var factory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        }));
        Exception failure = canceled
            ? new OperationCanceledException("private-cache-password")
            : new IOException("private-cache-password", new Exception("private-inner-token"));
        var cache = DispatchProxy.Create<IDistributedCache, PortProxy>();
        ((PortProxy)cache).Call = (method, _) =>
        {
            if (method!.Name == (write ? "SetAsync" : "GetAsync")) throw failure;
            return method.Name == "GetAsync" ? Task.FromResult<byte[]?>(null) : Task.CompletedTask;
        };
        using var handler = new RepositoryHandler();
        using var client = new HttpClient(handler);
        var service = new GitHubService(client, cache, factory.CreateLogger<GitHubService>());
        if (canceled && !write)
        {
            var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.GetUserRepositoriesAsync("private-access-token", false));
            Assert.Same(failure, thrown);
            Assert.Equal(0, handler.Calls);
        }
        else
        {
            var repositories = await service.GetUserRepositoriesAsync("private-access-token", false);
            Assert.Equal(canceled ? 0 : 1, repositories.Count);
            Assert.Equal(1, handler.Calls);
        }

        var warning = Assert.Single(exporter.Records, record =>
            record.Attributes.ContainsKey("FailureType"));
        Assert.Equal(failure.GetType().Name, warning.Attributes["FailureType"]);
        Assert.All(exporter.Records, record =>
        {
            Assert.Null(record.Exception);
            Assert.DoesNotContain("private", record.Message!);
            Assert.DoesNotContain("private", JsonSerializer.Serialize(record.Attributes));
        });
    }

    /// <summary>Network errors retain the empty-result contract without exporting request credentials or inner errors.</summary>
    [Fact]
    public async Task Repository_network_failure_does_not_export_exception_payload()
    {
        using var exporter = new SnapshotExporter();
        using var factory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        }));
        var cache = DispatchProxy.Create<IDistributedCache, PortProxy>();
        ((PortProxy)cache).Call = (_, _) => Task.FromResult<byte[]?>(null);
        using var handler = new RepositoryHandler
        {
            Failure = new HttpRequestException("private-url-token", new Exception("private-inner-token"))
        };
        using var client = new HttpClient(handler);
        var service = new GitHubService(client, cache, factory.CreateLogger<GitHubService>());
        Assert.Empty(await service.GetUserRepositoriesAsync("private-access-token", false));
        var error = Assert.Single(exporter.Records, record => record.Level == LogLevel.Error);
        Assert.Equal(nameof(HttpRequestException), error.Attributes["FailureType"]);
        Assert.Null(error.Exception);
        Assert.DoesNotContain("private", error.Message!);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(error.Attributes));
    }

    /// <summary>Supplies repository responses or synthetic failures without external requests.</summary>
    private sealed class RepositoryHandler : HttpMessageHandler
    {
        /// <summary>Number of API requests.</summary>
        public int Calls { get; private set; }

        /// <summary>Optional synthetic network failure.</summary>
        public Exception? Failure { get; init; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":42,"name":"fixture"}]""",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Fake application/framework ports with test-specific async responses.</summary>
    public class PortProxy : DispatchProxy
    {
        /// <summary>Response callback.</summary>
        public Func<MethodInfo?, object?[]?, object?> Call { get; set; } = null!;

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return Call(method, args);
        }
    }
}

/// <summary>Copies pooled SDK records during synchronous export and simulates collector rejection safely.</summary>
internal sealed class SnapshotExporter : BaseExporter<LogRecord>
{
    /// <summary>Detached records captured at the actual export boundary.</summary>
    internal ConcurrentQueue<ExportedLog> Records { get; } = new();

    /// <summary>Signals the first warning record.</summary>
    internal TaskCompletionSource Warning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Simulates an unavailable collector.</summary>
    internal bool Fail { get; set; }

    /// <summary>Resource configured for the actual provider.</summary>
    internal IReadOnlyDictionary<string, object>? ResourceAttributes { get; private set; }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        ResourceAttributes = ParentProvider.GetResource().Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var log in batch)
        {
            var scopes = new Dictionary<string, object?>();
            log.ForEachScope((scope, state) =>
            {
                foreach (var pair in scope) state[pair.Key] = pair.Value;
            }, scopes);
            Records.Enqueue(new ExportedLog(log.EventId, log.LogLevel, log.FormattedMessage,
                log.Attributes?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [], scopes, log.TraceId,
                log.Exception));
            if (log.LogLevel == LogLevel.Warning) Warning.TrySetResult();
        }

        return Fail ? ExportResult.Failure : ExportResult.Success;
    }
}

/// <summary>Immutable copy of an SDK export, avoiding access after pooled record reuse.</summary>
internal sealed record ExportedLog(
    EventId EventId,
    LogLevel Level,
    string? Message,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyDictionary<string, object?> Scopes,
    ActivityTraceId TraceId,
    Exception? Exception);