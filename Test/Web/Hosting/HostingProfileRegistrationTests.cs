using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Execution;
using Application.Abstractions.Ai.Policy;
using Application.Abstractions.Ai.Results;
using Application.Abstractions.GitHub;
using Application.Ai.Policy;
using Application.Diagnostics;
using Application.Orchestration.Cloud;
using Infrastructure.Ai.Processing;
using Infrastructure.Ai.Providers;
using Infrastructure.Diagnostics.Policy;
using Infrastructure.Observability;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Test.Web.Diagnostics;
using Web.Configs.Hosting;
using Web.Configs.Observability;

namespace Test.Web.Hosting;

/// <summary>
///     Verifies profile-specific validation using the production Web composition root.
/// </summary>
public sealed class HostingProfileRegistrationTests
{
    /// <summary>
    ///     Production composition exports all signals using SDK settings supplied by IDE launches,
    ///     preserves the custom endpoint override, and keeps unconfigured export disabled.
    /// </summary>
    /// <param name="standardEndpoint">Whether the IDE supplies the standard endpoint.</param>
    /// <param name="customEndpoint">Whether the operator supplies AutoMate's endpoint override.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Otlp_exports_all_signals_when_an_endpoint_is_configured(bool standardEndpoint, bool customEndpoint)
    {
        var builder = CreateBuilder("SelfHosted");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = standardEndpoint ? "http://ide.invalid:17011" : null,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OpenTelemetry:OtlpEndpoint"] = customEndpoint ? "http://collector.invalid:4318" : null
        });
        builder.AddApplicationServices();
        using var capture = new OtlpCaptureHandler();
        builder.Services.PostConfigureAll<OtlpExporterOptions>(options =>
            options.HttpClientFactory = () => new HttpClient(capture, false));
        using var services = builder.Services.BuildServiceProvider();
        var traces = services.GetRequiredService<TracerProvider>();
        var metrics = services.GetRequiredService<MeterProvider>();
        var logs = services.GetRequiredService<LoggerProvider>();

        using (AutoMateTelemetry.Deployments.StartActivity("deployment.local.execute"))
        {
        }

        using var meter = new Meter(AutoMateTelemetry.DeploymentMeter.Name);
        meter.CreateCounter<long>("automate.deployment.jobs.started").Add(1);
        services.GetRequiredService<ILoggerFactory>().CreateLogger("AppStartup")
            .LogInformation("[Startup] Database migrations are current.");

        Assert.True(traces.ForceFlush());
        Assert.True(metrics.ForceFlush());
        Assert.True(logs.ForceFlush());
        if (!standardEndpoint && !customEndpoint)
        {
            Assert.Empty(capture.Requests);
            return;
        }

        var host = customEndpoint ? "collector.invalid" : "ide.invalid";
        if (!customEndpoint)
            foreach (var signal in new[] { "logs", "traces", "metrics" })
                Assert.Contains(capture.Requests, request =>
                    request.Endpoint.Host == host && request.Endpoint.AbsolutePath == $"/v1/{signal}");
        Assert.All(capture.Requests, request => Assert.Equal(host, request.Endpoint.Host));
        Assert.Contains(capture.Requests, request => request.Body.Contains("deployment.local.execute"));
        Assert.Contains(capture.Requests, request => request.Body.Contains("automate.deployment.jobs.started"));
        Assert.Contains(capture.Requests, request => request.Body.Contains("Database migrations are current"));
    }

    /// <summary>
    ///     Azure reconnect requests newly declared delegated permissions without tenant-wide consent.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Azure_reconnect_requests_explicit_static_consent()
    {
        var builder = CreateBuilder("SelfHosted");

        builder.AddApplicationServices();
        await using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>()
            .Get("Microsoft");
        var http = new DefaultHttpContext();
        var context = new RedirectContext<OAuthOptions>(
            http, new AuthenticationScheme("Microsoft", null,
                typeof(OAuthHandler<OAuthOptions>)),
            options, new AuthenticationProperties(),
            "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?scope=https%3A%2F%2Fmanagement.azure.com%2F.default");

        await options.Events.OnRedirectToAuthorizationEndpoint(context);

        Assert.Contains("prompt=consent", http.Response.Headers.Location.ToString());
        Assert.DoesNotContain("adminconsent", http.Response.Headers.Location.ToString());
    }

    /// <summary>
    ///     Production composition selects Azure in either profile and checks its own key without
    ///     provider I/O.
    /// </summary>
    /// <param name="profile">Selected hosting profile label.</param>
    [Theory]
    [InlineData("SelfHosted")]
    [InlineData("SaaS")]
    public void Azure_provider_registration_uses_exact_resource_and_separate_credentials(string profile)
    {
        var certificatePath = profile == "SaaS" ? CreateTestCertificate() : null;

        try
        {
            var builder = CreateBuilder(profile);

            if (certificatePath is not null)
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SaaS:DataProtectionCertificatePath"] = certificatePath,
                    ["SaaS:DataProtectionCertificatePassword"] = "test-only"
                });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiAnalysis:AzureOpenAi:ResourceName"] = "automate-test",
                ["AiAnalysis:ApiKey"] = "direct-key-is-not-an-Azure-key"
            });
            builder.AddApplicationServices();
            using var services = builder.Services.BuildServiceProvider();
            using var scope = services.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetRequiredService<AzureOpenAiAnalysisProvider>());
            Assert.NotNull(services.GetRequiredService<ConsoleExceptionDiagnostics>());
            Assert.IsType<SafeLoggerFactory>(services.GetRequiredService<ILoggerFactory>());

            var catalog = services.GetRequiredService<AnalysisProviderCatalog>();
            var settings = new AiAnalysisOptions
            {
                Enabled = true,
                ProviderEgressEnabled = true,
                Provider = "azure-openai",
                RegionalProcessingApproved = true,
                ProcessingRegion = "eu",
                ApprovedRegions = ["eu"],
                ApprovedTenantIds = [Guid.NewGuid()],
                AllowedDataCategories = ["logs", "metrics", "traceCorrelation"],
                Endpoint = "https://automate-test.openai.azure.com/openai/v1/",
                Model = "analysis-deployment"
            };

            var selected = Assert.IsType<AnalysisProviderRegistration>(catalog.Select(settings));
            Assert.Equal(typeof(AzureOpenAiAnalysisProvider), selected.ImplementationType);
            Assert.False(selected.CredentialsConfigured!());

            builder.Configuration["AiAnalysis:AzureOpenAi:ApiKey"] = "synthetic-Azure-key";

            Assert.True(selected.CredentialsConfigured());

            builder.Configuration["AiAnalysis:AzureOpenAi:ResourceName"] = "other-resource";

            Assert.Null(catalog.Select(settings));
            Assert.False(services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value.Enabled);
        }
        finally
        {
            if (certificatePath is not null) File.Delete(certificatePath);
        }
    }

    /// <summary>
    ///     Shared quota/currency/precision limits fail startup even when AI is disabled.
    /// </summary>
    /// <param name="setting">Option key overridden by the scenario.</param>
    /// <param name="value">Synthetic input text.</param>
    [Theory]
    [InlineData("DailyTenantLimit", "-1")]
    [InlineData("DailyTenantLimit", "100001")]
    [InlineData("TenantRequestsPerMinute", "1001")]
    [InlineData("MaximumTenantProviderConcurrency", "17")]
    [InlineData("MaximumGlobalProviderConcurrency", "4097")]
    [InlineData("DailyTenantCostBudget", "-1")]
    [InlineData("DailyTenantCostBudget", "1000001")]
    [InlineData("MaximumProviderAttemptCost", "0.000000001")]
    [InlineData("BudgetCurrency", "usd")]
    [InlineData("BudgetCurrency", "private-secret")]
    public void Startup_rejects_invalid_shared_budget_limits(string setting, string value)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiAnalysis:" + setting] = value });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        var failure =
            Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IStartupValidator>().Validate());
        Assert.DoesNotContain("private-secret", failure.Message);
    }

    /// <summary>
    ///     Numeric limits fail startup even with AI disabled, without silent clamps or provider
    ///     activity.
    /// </summary>
    /// <param name="setting">Option key overridden by the scenario.</param>
    /// <param name="value">Synthetic input text.</param>
    [Theory]
    [InlineData("TimeoutSeconds", "4")]
    [InlineData("TimeoutSeconds", "301")]
    [InlineData("MaximumOutputTokens", "0")]
    [InlineData("MaximumOutputTokens", "8193")]
    [InlineData("MaximumContextCharacters", "0")]
    [InlineData("MaximumContextCharacters", "131073")]
    [InlineData("MaximumContextBytes", "0")]
    [InlineData("MaximumContextBytes", "131073")]
    [InlineData("MaximumContextTokens", "0")]
    [InlineData("MaximumContextTokens", "131073")]
    [InlineData("ResultRetentionDays", "0")]
    [InlineData("ResultRetentionDays", "91")]
    public void Ai_limits_fail_startup_when_out_of_range(string setting, string value)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiAnalysis:" + setting] = value });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    /// <summary>
    ///     Supported limits remain exact, and staging automatic analysis does not enable AI or egress.
    /// </summary>
    /// <param name="maximum">Configured bound or maximum aggregation variant.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ai_limit_boundaries_are_valid_without_egress(bool maximum)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAnalysis:TimeoutSeconds"] = maximum ? "300" : "5",
            ["AiAnalysis:MaximumOutputTokens"] = maximum ? "8192" : "1",
            ["AiAnalysis:MaximumContextCharacters"] = maximum ? "131072" : "1",
            ["AiAnalysis:MaximumContextBytes"] = maximum ? "131072" : "1",
            ["AiAnalysis:MaximumContextTokens"] = maximum ? "131072" : "1",
            ["AiAnalysis:ResultRetentionDays"] = maximum ? "90" : "1",
            ["AiAnalysis:AutomaticAnalysisEnabled"] = "true"
        });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        services.GetRequiredService<IStartupValidator>().Validate();
        var options = services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>().CurrentValue;

        Assert.Equal(maximum ? 8192 : 1, options.MaximumOutputTokens);
        Assert.Equal(maximum ? 300 : 5, options.TimeoutSeconds);
        Assert.False(options.Enabled);
        Assert.False(options.ProviderEgressEnabled);
    }

    /// <summary>
    ///     Invalid collector settings fail startup with fixed guidance instead of silently disabling
    ///     export or exposing credentials.
    /// </summary>
    /// <param name="endpoint">Parsed HTTP(S) collector endpoint, or null when invalid or absent.</param>
    [Theory]
    [InlineData("not-a-url-private-secret")]
    [InlineData("file:///private-secret")]
    [InlineData("https://user:private-secret@collector.example.invalid")]
    [InlineData("https://collector.example.invalid/?token=private-secret")]
    [InlineData("https://collector.example.invalid/#private-secret")]
    [InlineData(" https://collector.example.invalid/private-secret ")]
    [InlineData("https://collector.example.invalid/private secret")]
    public void Otlp_invalid_endpoint_fails_safely(string endpoint)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["OpenTelemetry:OtlpEndpoint"] = endpoint });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        var failure =
            Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("OtlpEndpoint", failure.Message);
        Assert.DoesNotContain("private-secret", failure.ToString());
    }

    /// <summary>
    ///     Collector URLs permit HTTPS and trusted-network HTTP without requiring any connection during
    ///     validation.
    /// </summary>
    /// <param name="endpoint">Parsed HTTP(S) collector endpoint, or null when invalid or absent.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://collector:4317")]
    [InlineData("https://collector.example.invalid:4317")]
    public void Otlp_endpoint_validation_does_not_contact_collector(string? endpoint)
    {
        var validator = new OpenTelemetryOptionsValidator();

        Assert.True(validator.Validate(null, new OpenTelemetryOptions { OtlpEndpoint = endpoint }).Succeeded);
    }

    /// <summary>
    ///     Resource-label validation never echoes invalid labels in startup failures.
    /// </summary>
    /// <param name="service">Fixture service-registration flag or service resource label.</param>
    /// <param name="oversized">Whether to exceed the bounded input size.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Otlp_resource_labels_are_bounded(bool service, bool oversized)
    {
        var invalid = oversized ? "private-secret" + new string('x', 101) : " ";
        var result = new OpenTelemetryOptionsValidator().Validate(null, new OpenTelemetryOptions
        {
            ServiceName = service ? invalid : "AutoMate",
            Environment = service ? null : invalid
        });

        Assert.True(result.Failed);
        Assert.DoesNotContain("private-secret", string.Join(" ", result.Failures!));
    }

    /// <summary>
    ///     Both profiles keep the existing output/timeout defaults and disabled export settings.
    /// </summary>
    /// <param name="services">Fixture dependency provider.</param>
    private static void AssertAnalysisDefaults(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value;

        Assert.Equal(8192, options.MaximumOutputTokens);
        Assert.Equal(60, options.TimeoutSeconds);
        Assert.Null(services.GetRequiredService<IOptions<OpenTelemetryOptions>>().Value.OtlpEndpoint);
    }

    /// <summary>
    ///     The production option binding rejects requested egress without explicit policy approval.
    /// </summary>
    [Fact]
    public void Ai_egress_configuration_fails_closed_when_approvals_are_missing()
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAnalysis:Enabled"] = "true",
            ["AiAnalysis:ProviderEgressEnabled"] = "true"
        });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>().CurrentValue);
    }

    /// <summary>
    ///     Both profiles resolve validation and safe analysis reads while AI egress remains disabled by
    ///     default.
    /// </summary>
    /// <param name="profile">Selected hosting profile label.</param>
    [Theory]
    [InlineData("SelfHosted")]
    [InlineData("SaaS")]
    public void Ai_result_boundaries_resolve_without_provider_credentials(string profile)
    {
        var certificatePath = profile == "SaaS" ? CreateTestCertificate() : null;

        try
        {
            var builder = CreateBuilder(profile);

            if (certificatePath is not null)
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SaaS:DataProtectionCertificatePath"] = certificatePath,
                    ["SaaS:DataProtectionCertificatePassword"] = "test-only"
                });
            builder.AddApplicationServices();
            using var exporter = new SnapshotExporter();

            builder.Logging.AddOpenTelemetry(logging =>
                logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
            using var services = builder.Services.BuildServiceProvider();
            using var scope = services.CreateScope();

            Assert.Contains(services.GetServices<IHostedService>(),
                hosted => hosted is DeploymentAnalysisRetentionService);
            Assert.Contains(services.GetServices<IHostedService>(),
                hosted => hosted is FailedDeploymentAnalysisDispatcher);
            Assert.NotNull(services.GetRequiredService<IAnalysisResultValidator>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisService>());
            Assert.Same(scope.ServiceProvider.GetRequiredService<DeploymentAnalysisService>(),
                scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILlmAnalysisProvider>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisContextBuilder>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisReadiness>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAnalysisBudgetGuard>());

            var health = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

            var readiness = Assert.Single(health.Registrations, item => item.Name == "ai_analysis");
            Assert.Contains(AnalysisHealthChecks.ReadinessTag, readiness.Tags);
            Assert.Equal(TimeSpan.FromSeconds(5), readiness.Timeout);
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAnalysisEgressAuthorizer>());
            Assert.False(services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value.Enabled);

            AssertAnalysisDefaults(services);

            Assert.IsType<SecurityAuditResultHandler>(
                services.GetRequiredService<IAuthorizationMiddlewareResultHandler>());

            var deployment = Guid.NewGuid();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("fixture");

            using (OperationalLog.BeginCorrelation(logger, deployment))
            {
                OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Completed);
            }

            Assert.Equal(deployment, Assert.Single(exporter.Records).Scopes["DeploymentId"]);
            Assert.Equal("AutoMate", exporter.ResourceAttributes!["service.name"]);
            Assert.Equal("Testing", exporter.ResourceAttributes["deployment.environment"]);
            Assert.Equal(profile, exporter.ResourceAttributes["automate.hosting_profile"]);
            Assert.True(exporter.ResourceAttributes.ContainsKey("service.version"));
            Assert.Equal(4, exporter.ResourceAttributes.Count);
            Assert.Equal(exporter.ResourceAttributes.OrderBy(pair => pair.Key),
                services.GetRequiredService<TracerProvider>().GetResource().Attributes.OrderBy(pair => pair.Key));
            Assert.Equal(exporter.ResourceAttributes.OrderBy(pair => pair.Key),
                services.GetRequiredService<MeterProvider>().GetResource().Attributes.OrderBy(pair => pair.Key));

            using (logger.BeginScope(new Dictionary<string, object?> { ["Cookie"] = "private-cookie" }))
            {
                logger.LogWarning(new Exception("private-exception"), "Unreviewed private-message {Url}",
                    "https://private.example/?token=private-token");
            }

            var sanitized = exporter.Records.Last();

            Assert.Equal(PlatformTelemetryPolicy.UnknownMessage, sanitized.Message);
            Assert.DoesNotContain("Cookie", sanitized.Scopes.Keys);
            Assert.NotNull(services.GetRequiredService<TracerProvider>());

            using var signalR = new ActivitySource("Microsoft.AspNetCore.SignalR.Server");
            using var invocation = signalR.StartActivity("fixture.invocation");

            Assert.NotNull(invocation);
        }
        finally
        {
            if (certificatePath is not null) File.Delete(certificatePath);
        }
    }

    /// <summary>
    ///     Invalid diagnostic limits fail safely before hosted collectors or transport sends start.
    /// </summary>
    /// <param name="setting">Option key overridden by the scenario.</param>
    /// <param name="value">Synthetic input text.</param>
    [Theory]
    [InlineData("BufferCapacity", "0")]
    [InlineData("PersistenceTimeoutSeconds", "0")]
    [InlineData("DeliveryTimeoutSeconds", "31")]
    public void Diagnostic_sink_bounds_are_validated_at_startup(string setting, string value)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { [$"DeploymentDiagnostics:{setting}"] = value });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<DeploymentDiagnosticOptions>>().Value);
        Assert.Contains("DeploymentDiagnostics", failure.Message);
    }

    /// <summary>
    ///     Disabled AI still rejects unsafe retry configuration at startup without contacting
    ///     providers.
    /// </summary>
    /// <param name="setting">Option key overridden by the scenario.</param>
    /// <param name="value">Synthetic input text.</param>
    [Theory]
    [InlineData("MaximumProviderRetries", "6")]
    [InlineData("MaximumProviderRetries", "-1")]
    [InlineData("RetryBaseDelaySeconds", "0")]
    [InlineData("RetryBaseDelaySeconds", "301")]
    [InlineData("RetryMaximumDelaySeconds", "4")]
    [InlineData("RetryMaximumDelaySeconds", "3601")]
    [InlineData("RetryMaximumDelaySeconds", "5")]
    public void Startup_rejects_invalid_retry_bounds(string setting, string value)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { [$"AiAnalysis:{setting}"] = value });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value);
        Assert.Contains("AI retries", error.Message);
    }

    /// <summary>
    ///     Daily admission limits are bounded at startup even with AI disabled; zero is a valid deny-
    ///     new-work setting.
    /// </summary>
    /// <param name="limit">Requested count or configured admission bound.</param>
    /// <param name="valid">Whether the fixture input is intended to pass validation.</param>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(1001, false)]
    [InlineData(0, true)]
    public void Startup_validates_daily_analysis_limit(int limit, bool valid)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiAnalysis:DailyProjectLimit"] = limit.ToString() });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        if (valid) Assert.Equal(0, services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value.DailyProjectLimit);
        else
            Assert.Contains("daily project admission", Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value).Message);
    }

    /// <summary>
    ///     Worker slot bounds are validated even with AI disabled, including both supported boundaries.
    /// </summary>
    /// <param name="concurrency">Configured parallel-operation limit.</param>
    /// <param name="valid">Whether the fixture input is intended to pass validation.</param>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(17, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(16, true)]
    public void Startup_validates_analysis_concurrency(int concurrency, bool valid)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiAnalysis:MaximumConcurrency"] = concurrency.ToString() });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        if (valid)
            Assert.Equal(concurrency,
                services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value.MaximumConcurrency);
        else
            Assert.Contains("worker concurrency", Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value).Message);
    }

    /// <summary>
    ///     Dashboard dependencies resolve on self-hosted installations without SaaS credentials.
    /// </summary>
    [Fact]
    public void SelfHosted_resolves_shared_cloud_services_without_GitHub_App_settings()
    {
        var builder = CreateBuilder("SelfHosted");

        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();

        var storage = services.GetRequiredService<IOptions<TelemetryStorageOptions>>().Value;


        Assert.True(storage.DiskGateway);
        Assert.True(storage.Specialized);

        services.GetRequiredService<IStartupValidator>().Validate();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IGitHubAppCredentials>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICloudDeploymentRunService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICloudDeploymentOrchestrator>());
    }

    /// <summary>
    ///     A missing self-hosted gateway fails validation instead of enabling an implicit database
    ///     fallback.
    /// </summary>
    [Fact]
    public void SelfHosted_requires_gateway_configuration()
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TelemetryStorage:GatewayUrl"] = "" });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        var failure =
            Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("GatewayUrl", failure.Message);
    }

    /// <summary>
    ///     Self-hosted startup cannot silently restore raw PostgreSQL payload writes.
    /// </summary>
    /// <param name="backend">Telemetry backend selected by the scenario.</param>
    /// <param name="deliveryMode">Telemetry delivery mode selected by the scenario.</param>
    [Theory]
    [InlineData("Postgres", "PostgresOutbox")]
    [InlineData("LokiMimir", "PostgresOutbox")]
    public void SelfHosted_rejects_database_payload_persistence(string backend, string deliveryMode)
    {
        var builder = CreateBuilder("SelfHosted");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TelemetryStorage:Backend"] = backend,
            ["TelemetryStorage:DeliveryMode"] = deliveryMode
        });
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<TelemetryStorageOptions>>().Value);
        Assert.Contains("both SelfHosted and SaaS", failure.Message);
    }

    /// <summary>
    ///     SaaS still rejects missing GitHub App secrets during startup validation.
    /// </summary>
    [Fact]
    public void SaaS_startup_requires_GitHub_App_settings()
    {
        var certificatePath = CreateTestCertificate();

        try
        {
            var builder = CreateBuilder("SaaS");

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SaaS:DataProtectionCertificatePath"] = certificatePath,
                ["SaaS:DataProtectionCertificatePassword"] = "test-only"
            });
            builder.AddApplicationServices();
            using var services = builder.Services.BuildServiceProvider();

            var failure = Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IStartupValidator>().Validate());
            Assert.Contains("configured GitHub App", failure.Message);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    /// <summary>
    ///     Creates a disposable certificate for encrypted-key registration without touching production
    ///     keys.
    /// </summary>
    /// <returns>The synthetic text produced for this scenario.</returns>
    private static string CreateTestCertificate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"automate-profile-{Guid.NewGuid():N}.pfx");
        using var key = RSA.Create(2048);
        var request =
            new CertificateRequest("CN=AutoMate test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate =
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));

        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "test-only"));

        return path;
    }

    /// <summary>
    ///     Uses isolated configuration and fake connection settings without contacting providers.
    /// </summary>
    /// <param name="mode">Selected legacy, provider or fixture behavior mode.</param>
    /// <returns>The configured web application builder instance or response for this scenario.</returns>
    private static WebApplicationBuilder CreateBuilder(string mode)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(ServiceConfiguration).Assembly.GetName().Name
        });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostingProfile:Mode"] = mode,
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["Authentication:GitHub:ClientId"] = "test-client",
            ["Authentication:GitHub:ClientSecret"] = "test-secret",
            ["Authentication:Microsoft:ClientId"] = "test-client",
            ["Authentication:Microsoft:ClientSecret"] = "test-secret",
            ["TelemetryStorage:GatewayUrl"] = "https://telemetry.example.invalid",
            ["TelemetryStorage:GatewayToken"] = "test-only-credential-not-for-production",
            ["TelemetryStorage:LokiUrl"] = "https://logs.example.invalid",
            ["TelemetryStorage:MetricsWriteUrl"] = "https://metrics.example.invalid/otlp/v1/metrics",
            ["TelemetryStorage:MetricsQueryUrl"] = "https://metrics.example.invalid/prometheus"
        });

        return builder;
    }

    /// <summary>
    ///     Captures actual OTLP protobuf requests without contacting a collector or starting host workers.
    /// </summary>
    private sealed class OtlpCaptureHandler : HttpMessageHandler
    {
        /// <summary>Serialized signal requests and their resolved collector endpoints.</summary>
        public ConcurrentQueue<(Uri Endpoint, string Body)> Requests { get; } = new();

        /// <summary>Handles the metric exporter's synchronous HTTP transport in the test fixture.</summary>
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            request.Content!.ReadAsStream(cancellationToken).CopyTo(bytes);
            Requests.Enqueue((request.RequestUri!, Encoding.UTF8.GetString(bytes.ToArray())));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }

        /// <summary>Records a packet and returns an empty successful OTLP protobuf response.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Requests.Enqueue((request.RequestUri!, Encoding.UTF8.GetString(bytes)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
        }
    }
}
