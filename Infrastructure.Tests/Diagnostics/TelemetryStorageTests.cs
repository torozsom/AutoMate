using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using FluentAssertions;
using Infrastructure.Diagnostics;
using Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Credential-free storage protocol, quota configuration, and numeric-unit regression tests.</summary>
public sealed class TelemetryStorageTests
{
    /// <summary>Docker CPU usage preserves multiple cores and binary memory units.</summary>
    [Fact]
    public void Docker_numeric_samples_preserve_units_and_missing_values()
    {
        var samples = DeploymentMetricNormalizer.Docker("250.5%", "1.5GiB / 2GB");
        samples.Single(s => s.Unit == "cores").Value.Should().Be(2.505);
        samples.Single(s => s.Name == "automate_memory_used_bytes").Value.Should().Be(1.5 * 1073741824);
        samples.Single(s => s.Name == "automate_memory_limit_bytes").Value.Should().Be(2e9);
        DeploymentMetricNormalizer.Docker("unknown", "unknown").Should().BeEmpty();
    }

    /// <summary>Plaintext managed egress and unapproved providers are rejected.</summary>
    [Fact]
    public void Configuration_requires_finite_limits_and_approved_managed_transport()
    {
        new TelemetryStorageOptions().IsValid().Should().BeTrue();
        new TelemetryStorageOptions { Backend = "Other" }.IsValid().Should().BeFalse();
        var options = Specialized();
        options.IsValid().Should().BeTrue();
        options.ManagedService = true;
        options.ManagedDataProcessingApproved = true;
        options.ProcessingRegion = "EU";
        options.IsValid().Should().BeFalse();
    }

    /// <summary>A backend-only configuration names missing endpoints and never echoes credential-bearing URLs.</summary>
    [Fact]
    public void Startup_validation_identifies_missing_settings_without_exposing_values()
    {
        var validator = new TelemetryStorageOptionsValidator();
        var result = validator.Validate(null, new TelemetryStorageOptions { Backend = "LokiMimir" });
        result.Failed.Should().BeTrue();
        string.Join(' ', result.Failures!).Should().Contain("LokiUrl").And.Contain("MetricsWriteUrl").And
            .Contain("MetricsQueryUrl");
        var options = Specialized();
        options.LokiUrl = "https://user:private-password@example.invalid";
        string.Join(' ', validator.Validate(null, options).Failures!).Should().NotContain("private-password");
        validator.Validate(null, new TelemetryStorageOptions()).Succeeded.Should().BeTrue();
    }

    /// <summary>Envelope identities belong in structured metadata rather than stream labels.</summary>
    [Fact]
    public async Task Loki_push_uses_stable_ids_tenant_headers_and_structured_metadata()
    {
        var envelope = Envelope();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            request.Headers.GetValues("X-Scope-OrgID").Single().Should().Be(envelope.TenantId.ToString("N"));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var stream = body.RootElement.GetProperty("streams")[0];
            stream.GetProperty("stream").EnumerateObject().Select(p => p.Name).Should().NotContain("deployment_id");
            stream.GetProperty("values")[0][2].GetProperty("event_id").GetString().Should()
                .Be(envelope.EventId.ToString("N"));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var transport =
            new TelemetryHttpTransport(new StubHttpClientFactory(handler), Options.Create(Specialized()));
        var adapter = new LokiDeploymentLogs(transport, Options.Create(Specialized()));
        await adapter.WriteAsync([envelope], CancellationToken.None);
        await adapter.WriteAsync([envelope], CancellationToken.None);
    }

    /// <summary>Backend copies are deduplicated and cursor/time filtering stays server-owned.</summary>
    [Fact]
    public async Task Loki_query_deduplicates_retry_copies_and_scopes_project_and_cursor()
    {
        var envelope = Envelope();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            query.Should().Contain(envelope.Event.ProjectId.ToString("N")).And.Contain("order_id > 10").And
                .Contain("expires_at >");
            return DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                status = "success",
                data = new
                {
                    result = new[]
                    {
                        new
                        {
                            values = new[]
                            {
                                new[] { "1", JsonSerializer.Serialize(envelope, TelemetryHttpTransport.Json) },
                                new[] { "1", JsonSerializer.Serialize(envelope, TelemetryHttpTransport.Json) }
                            }
                        }
                    }
                }
            }));
        });
        using var transport =
            new TelemetryHttpTransport(new StubHttpClientFactory(handler), Options.Create(Specialized()));
        var adapter = new LokiDeploymentLogs(transport, Options.Create(Specialized()));
        (await adapter.ReadAsync(envelope.TenantId, envelope.Event.ProjectId, envelope.Event.DeploymentId!.Value,
            10, false, 500, CancellationToken.None)).Should().ContainSingle();
    }

    /// <summary>Metric requests use numeric OTLP fields and report partial rejection.</summary>
    [Fact]
    public async Task Mimir_partial_success_does_not_acknowledge_lost_samples()
    {
        var envelope = Envelope() with
        {
            Event = Envelope().Event with
            {
                Metrics = [new DeploymentMetricSample("automate_cpu_usage_cores", 2.5, "cores")]
            }
        };
        var handler = new DelegateHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            body.RootElement.GetProperty("resourceMetrics")[0].GetProperty("scopeMetrics")[0]
                .GetProperty("metrics")[0].GetProperty("gauge").GetProperty("dataPoints")[0]
                .GetProperty("asDouble").GetDouble().Should().Be(2.5);
            return DelegateHttpMessageHandler.Json("{\"partialSuccess\":{\"rejectedDataPoints\":\"1\"}}");
        });
        using var transport =
            new TelemetryHttpTransport(new StubHttpClientFactory(handler), Options.Create(Specialized()));
        var adapter = new MimirDeploymentMetrics(transport, Options.Create(Specialized()));
        await adapter.Invoking(a => a.WriteAsync([envelope], CancellationToken.None)).Should()
            .ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Throttling retains safe retry guidance without returning provider bodies.</summary>
    [Fact]
    public async Task Provider_throttling_preserves_retry_after()
    {
        var handler = new DelegateHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        });
        using var transport =
            new TelemetryHttpTransport(new StubHttpClientFactory(handler), Options.Create(Specialized()));
        var error = await transport
            .Invoking(t => t.SendAsync("http://localhost/query", Guid.NewGuid(), null, CancellationToken.None))
            .Should().ThrowAsync<TelemetryProviderException>();
        error.Which.RetryAfter.Should().Be(TimeSpan.FromMinutes(2));
    }

    /// <summary>Constructs private-test endpoints.</summary>
    internal static TelemetryStorageOptions Specialized()
    {
        return new TelemetryStorageOptions
        {
            Backend = "LokiMimir",
            LokiUrl = "http://localhost:24310",
            MetricsWriteUrl = "http://localhost:24909/otlp/v1/metrics",
            MetricsQueryUrl = "http://localhost:24909/prometheus",
            AllowInsecureDevelopment = true
        };
    }

    /// <summary>Constructs a safe immutable envelope.</summary>
    internal static DeploymentLogEnvelope Envelope()
    {
        return new DeploymentLogEnvelope(Guid.NewGuid(), Guid.NewGuid(), 11,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), new DeploymentDiagnosticEvent(Guid.NewGuid(),
                Guid.NewGuid(),
                DeploymentDiagnosticSource.DockerCompose, DeploymentDiagnosticKind.Log,
                DeploymentDiagnosticSeverity.Information,
                DateTimeOffset.UtcNow, "safe line", new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build)),
            "build");
    }
}