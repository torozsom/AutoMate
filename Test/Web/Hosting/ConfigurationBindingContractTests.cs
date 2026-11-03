using System.Text.Json;
using Application.Diagnostics;
using Application.Orchestration.Cloud;
using Application.Orchestration.Jobs;
using Infrastructure.Azure.Monitor;
using Infrastructure.Diagnostics.Policy;
using Infrastructure.Diagnostics.Storage;
using Infrastructure.Docker.Runtime;
using Infrastructure.Email;
using Infrastructure.GitHub.Authentication;
using Infrastructure.Observability.OpenTelemetry;
using Microsoft.Extensions.Configuration;

namespace Test.Web.Hosting;

/// <summary>Protects non-default configuration values across construction and runtime configuration binding.</summary>
public sealed class ConfigurationBindingContractTests
{
    /// <summary>
    ///     Export activation and sampling thresholds remain configurable through the real host binder.
    /// </summary>
    [Fact]
    public void OpenTelemetryOptions_preserves_configured_values()
    {
        AssertBinding("OpenTelemetry", new OpenTelemetryOptions
        {
            ServiceName = "AutoMate.Fixture",
            Environment = "Testing",
            ExportConsole = true,
            OtlpEndpoint = "https://collector.example.test",
            SignalRSuccessSampleRate = 0.5,
            EmptyClaimSampleRate = 0.2,
            SignalRSlowMilliseconds = 200,
            ClaimSlowMilliseconds = 500
        });
    }

    /// <summary>
    ///     Live cadence and sink limits retain nondefault operator configuration.
    /// </summary>
    [Fact]
    public void DeploymentDiagnosticOptions_preserves_configured_values()
    {
        AssertBinding("DeploymentDiagnostics", new DeploymentDiagnosticOptions
        {
            BufferCapacity = 128,
            PersistenceTimeoutSeconds = 20,
            DeliveryTimeoutSeconds = 4,
            LiveMetricIntervalMilliseconds = 1500
        });
    }

    /// <summary>All configured CloudSaas values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void CloudSaasOptions_preserves_configured_values()
    {
        AssertBinding("CloudSaas", new CloudSaasOptions
        {
            MaxQueuedPerUser = 17,
            MaxActivePerUser = 3,
            MaxActivePerInstallation = 5,
            MaxActiveGlobally = 40,
            MaxActivePerWorker = 6,
            SchedulerPollSeconds = 4
        });
    }

    /// <summary>All configured DeploymentConcurrency values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void DeploymentConcurrencyOptions_preserves_configured_values()
    {
        AssertBinding("DeploymentConcurrency", new DeploymentConcurrencyOptions
        {
            MaxQueuedJobs = 200,
            MaxCloudDeployments = 3,
            MaxLocalBuilds = 2,
            MaxLocalStops = 5
        });
    }

    /// <summary>All configured AzureMonitorLogs values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void AzureMonitorLogsOptions_preserves_configured_values()
    {
        AssertBinding("AzureMonitorLogs", new AzureMonitorLogsOptions
        {
            TokenEndpoint = "https://identity.example.test/token",
            ClientId = "fixture-client",
            ClientSecret = "synthetic-secret",
            Scope = "https://logs.example.test/.default",
            BatchSize = 123,
            MaximumPagesPerPoll = 8,
            OverlapWindow = TimeSpan.FromMinutes(3),
            InitialLookback = TimeSpan.FromMinutes(20),
            DatabaseInitialLookback = TimeSpan.FromHours(12),
            FreshnessWarningAge = TimeSpan.FromMinutes(8)
        });
    }

    /// <summary>All configured TelemetryStorage values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void TelemetryStorageOptions_preserves_configured_values()
    {
        AssertBinding("TelemetryStorage", new TelemetryStorageOptions
        {
            Backend = "LokiMimir",
            DeliveryMode = "DiskGateway",
            GatewayUrl = "https://gateway.example.test",
            GatewayToken = new string('s', 40),
            LokiUrl = "https://loki.example.test",
            MetricsWriteUrl = "https://metrics.example.test/write",
            MetricsQueryUrl = "https://metrics.example.test/query",
            Authorization = "Bearer synthetic-token",
            CaCertificatePath = "synthetic-ca.pem",
            AllowInsecureDevelopment = true,
            ManagedService = true,
            ManagedDataProcessingApproved = true,
            ProcessingRegion = "eu",
            BufferHours = 12,
            TenantBufferBytes = 100_000L,
            GlobalBufferBytes = 200_000L,
            TenantBytesPerMinute = 20_000L,
            MaximumMetricContainers = 12,
            BatchSize = 17,
            QueryConcurrency = 3,
            RuntimeSampleSeconds = 120
        });
    }

    /// <summary>All configured DiskSpool values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void DiskSpoolOptions_preserves_configured_values()
    {
        AssertBinding("DiskSpool", new DiskSpoolOptions
        {
            Directory = Path.Combine(Path.GetTempPath(), "automate-binding-fixture"),
            BatchSize = 7
        });
    }

    /// <summary>All configured Docker values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void DockerOptions_preserves_configured_values()
    {
        AssertBinding("Docker", new DockerOptions
        {
            DefaultContainerPort = 8081,
            ComposeTimeoutMinutes = 12,
            WindowsDockerUri = "npipe://./pipe/fixture",
            UnixDockerUri = "unix:///tmp/fixture.sock",
            DefaultDockerIgnore = ["output/", "cache/"]
        });
    }

    /// <summary>All configured Email values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void EmailOptions_preserves_configured_values()
    {
        AssertBinding("Email", new EmailOptions
        {
            SenderEmail = "sender@example.test",
            AppPassword = "synthetic-password",
            SenderName = "Fixture",
            SmtpHost = "smtp.example.test",
            SmtpPort = 2525
        });
    }

    /// <summary>All configured GitHubApp values survive binding instead of reverting to defaults.</summary>
    [Fact]
    public void GitHubAppOptions_preserves_configured_values()
    {
        AssertBinding("GitHubApp", new GitHubAppOptions
        {
            AppId = 123L,
            AppSlug = "fixture-app",
            PrivateKeyPem = "synthetic-key",
            WebhookSecret = "synthetic-webhook-secret"
        });
    }

    /// <summary>Builds real key/value configuration, including array indexes, and compares the complete bound shape.</summary>
    private static void AssertBinding<T>(string section, T expected)
    {
        var values = new Dictionary<string, string?>();
        foreach (var property in JsonSerializer.SerializeToElement(expected).EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in property.Value.EnumerateArray())
                    values[$"{section}:{property.Name}:{index++}"] = item.ToString();
            }
            else
            {
                values[$"{section}:{property.Name}"] = property.Value.ToString();
            }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var actual = configuration.GetSection(section).Get<T>();
        Assert.NotNull(actual);
        var expectedJson = JsonSerializer.SerializeToElement(expected);
        var actualJson = JsonSerializer.SerializeToElement(actual);
        var defaults = JsonSerializer.SerializeToElement(Activator.CreateInstance<T>());
        foreach (var property in expectedJson.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                // The configuration binder appends configured array elements to constructor defaults.
                var expectedItems = defaults.GetProperty(property.Name).EnumerateArray()
                    .Concat(property.Value.EnumerateArray()).Select(item => item.GetRawText());
                Assert.Equal(expectedItems,
                    actualJson.GetProperty(property.Name).EnumerateArray().Select(item => item.GetRawText()));
            }
            else
            {
                Assert.Equal(property.Value.GetRawText(), actualJson.GetProperty(property.Name).GetRawText());
            }
    }
}
