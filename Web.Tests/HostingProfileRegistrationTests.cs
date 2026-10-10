using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Application.Abstractions.Ai;
using Application.Abstractions.GitHub;
using Application.Ai;
using Application.Diagnostics;
using Application.Orchestration;
using Infrastructure.Ai;
using Infrastructure.Diagnostics;
using Infrastructure.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Web.Configs;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies profile-specific validation using the production Web composition root.</summary>
public sealed class HostingProfileRegistrationTests
{
    /// <summary>Production composition selects Azure in either profile and checks its own key without provider I/O.</summary>
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

    /// <summary>Shared quota/currency/precision limits fail startup even when AI is disabled.</summary>
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

    /// <summary>Numeric limits fail startup even with AI disabled, without silent clamps or provider activity.</summary>
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

    /// <summary>Supported limits remain exact, and staging automatic analysis does not enable AI or egress.</summary>
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
    ///     Invalid collector settings fail startup with fixed guidance instead of silently disabling export or exposing
    ///     credentials.
    /// </summary>
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

    /// <summary>Collector URLs permit HTTPS and trusted-network HTTP without requiring any connection during validation.</summary>
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

    /// <summary>Resource-label validation never echoes invalid labels in startup failures.</summary>
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

    /// <summary>Both profiles keep the existing output/timeout defaults and disabled export settings.</summary>
    private static void AssertAnalysisDefaults(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value;
        Assert.Equal(8192, options.MaximumOutputTokens);
        Assert.Equal(60, options.TimeoutSeconds);
        Assert.Null(services.GetRequiredService<IOptions<OpenTelemetryOptions>>().Value.OtlpEndpoint);
    }

    /// <summary>The production option binding rejects requested egress without explicit policy approval.</summary>
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

    /// <summary>Both profiles resolve validation and safe analysis reads while AI egress remains disabled by default.</summary>
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

    /// <summary>Invalid diagnostic limits fail safely before hosted collectors or transport sends start.</summary>
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

    /// <summary>Disabled AI still rejects unsafe retry configuration at startup without contacting providers.</summary>
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

    /// <summary>Daily admission limits are bounded at startup even with AI disabled; zero is a valid deny-new-work setting.</summary>
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

    /// <summary>Worker slot bounds are validated even with AI disabled, including both supported boundaries.</summary>
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

    /// <summary>Dashboard dependencies resolve on self-hosted installations without SaaS credentials.</summary>
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

    /// <summary>A missing self-hosted gateway fails validation instead of enabling an implicit database fallback.</summary>
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

    /// <summary>Self-hosted startup cannot silently restore raw PostgreSQL payload writes.</summary>
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

    /// <summary>SaaS still rejects missing GitHub App secrets during startup validation.</summary>
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

    /// <summary>Creates a disposable certificate for encrypted-key registration without touching production keys.</summary>
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

    /// <summary>Uses isolated configuration and fake connection settings without contacting providers.</summary>
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
}