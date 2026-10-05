using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Application.Diagnostics;
using FluentAssertions;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

public sealed class DiagnosticRedactorTests
{
    /// <summary>Supported secret formats are masked without erasing surrounding progress output.</summary>
    [Theory]
    [InlineData("{\"password\":\"private-value\"}")]
    [InlineData("{\"api-key\": \"private-value\"}")]
    [InlineData("{\"refresh_token\":\"private-value\"}")]
    [InlineData("{\"Authorization\":\"Bearer private-value\"}")]
    [InlineData("{\"Cookie\":\"session=private-value\"}")]
    [InlineData("DATABASE_PASSWORD='private-value with spaces'")]
    [InlineData("AWS_SECRET_ACCESS_KEY=private-value")]
    [InlineData("DB_PASS=private-value")]
    [InlineData("AccountKey=private-value")]
    [InlineData("https://example.invalid/path?sig=private-value&safe=yes")]
    [InlineData("passphrase=private-value")]
    [InlineData("ConnectionString=Server=host;Password=private-value;Database=sample")]
    [InlineData("Cookie: session=private-value; other=private-other")]
    [InlineData("Set-Cookie: session=private-value; HttpOnly")]
    [InlineData("Authorization: Basic private-value")]
    [InlineData("Bearer private-value")]
    [InlineData("https://user:private-value@example.invalid/path")]
    [InlineData("https://example.invalid/path?api_key=private-value&safe=yes")]
    [InlineData("github_pat_private123456789012345678901234567890")]
    [InlineData("ghp_private123456789012345678901234567890")]
    [InlineData("sk-proj-private123456789012345678901234567890")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nprivate-value\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN PRIVATE KEY-----\\nprivate-value\\n-----END PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nprivate-value")]
    [InlineData("password=\"private-value")]
    [InlineData("pass\u001b[31mword=private-value")]
    public void Masking_is_idempotent_across_supported_formats(string sensitive)
    {
        var redactor = new DiagnosticRedactor();
        var safe = redactor.RedactText("progress\r\n" + sensitive);
        Assert.StartsWith("progress\r\n", safe);
        Assert.DoesNotContain("private", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", safe);
        Assert.Equal(safe, redactor.RedactText(safe));
    }

    /// <summary>Every string field is sanitized; GUIDs, numeric samples, ordering and legal routing remain intact.</summary>
    [Fact]
    public void Event_metadata_keys_and_metrics_cannot_bypass_masking()
    {
        var original = new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(),
            DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticKind.Metric,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, "safe",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, "web"),
            new Dictionary<string, string>
            {
                ["api-key"] = "private-key",
                ["Connection_String"] = "private-connection",
                ["PRIVATE_KEY"] = "private-pem",
                ["Cookie"] = "private-cookie",
                ["password=private-key-name"] = "private-value",
                ["revision"] = "v1"
            }, "private-trace", "private-span", 17,
            "Bearer private-cursor",
            new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Web,
                DeploymentDiagnosticStream.Metric, "token=private-instance"),
            [
                new DeploymentMetricSample("cpu", 0.5, "cores"),
                new DeploymentMetricSample("api_key=private-name", 2, "password=private-unit")
            ]);
        var result = new DiagnosticRedactor().Redact(original);
        var json = JsonSerializer.Serialize(result.Event);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.Equal(original.ProjectId, result.Event.ProjectId);
        Assert.Equal(original.DeploymentId, result.Event.DeploymentId);
        Assert.Equal(17, result.Event.Sequence);
        Assert.Equal(original.Metrics![0], result.Event.Metrics![0]);
        Assert.Equal(2, result.Event.Metrics[1].Value);
        Assert.Equal("v1", result.Event.Attributes!["revision"]);
        Assert.True(result.RedactedValueCount >= 10);
        Assert.Equal(0, new DiagnosticRedactor().Redact(result.Event).RedactedValueCount);
    }

    /// <summary>Input caps fail closed, while ordinary long output stays bounded with a stable omission marker.</summary>
    [Fact]
    public void Text_bounds_do_not_leak_or_change_on_repeated_redaction()
    {
        var redactor = new DiagnosticRedactor();
        Assert.Equal("[REDACTED]", redactor.RedactText(new string('x', 131_073) + "password=private-value"));
        var bounded = redactor.RedactText(new string('x', 8_000) + "password=private-value");
        Assert.Equal(4096, bounded.Length);
        Assert.Contains("[output truncated]", bounded);
        Assert.Equal(bounded, redactor.RedactText(bounded));
        Assert.Equal("blank\r\n\rprogress\n", redactor.RedactText("blank\r\n\rprogress\n"));
    }

    [Fact]
    public async Task Publisher_redacts_before_placing_an_event_on_the_delivery_queue()
    {
        var publisher = new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(new DeploymentDiagnosticOptions { BufferCapacity = 16 }),
            NullLogger<DeploymentDiagnosticPublisher>.Instance);
        var diagnosticEvent = new DeploymentDiagnosticEvent(Guid.NewGuid(), null,
            DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, "password=not-for-clients",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"));

        await publisher.PublishAsync(diagnosticEvent);
        var queued = await publisher.Reader.ReadAsync();

        queued.Message.Should().Be("password=[REDACTED]");
        queued.TraceId.Should().NotBeNullOrWhiteSpace();
        queued.SpanId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Publisher_accepts_a_blank_GitHub_log_line()
    {
        var publisher = new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(new DeploymentDiagnosticOptions { BufferCapacity = 16 }),
            NullLogger<DeploymentDiagnosticPublisher>.Instance);
        var diagnosticEvent = new DeploymentDiagnosticEvent(Guid.NewGuid(), null,
            DeploymentDiagnosticSource.GitHubActions, DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, "\r\n",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build));

        await publisher.PublishAsync(diagnosticEvent);

        (await publisher.Reader.ReadAsync()).Message.Should().Be("\r\n");
    }

    [Fact]
    public async Task Publisher_reports_bounded_queue_overflow_for_a_durable_gap_marker()
    {
        var publisher = new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(new DeploymentDiagnosticOptions { BufferCapacity = 16 }),
            NullLogger<DeploymentDiagnosticPublisher>.Instance);
        var projectId = Guid.NewGuid();
        for (var index = 0; index < 17; index++)
            await publisher.PublishAsync(new DeploymentDiagnosticEvent(projectId, Guid.NewGuid(),
                DeploymentDiagnosticSource.DockerCompose, DeploymentDiagnosticKind.Log,
                DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, $"line {index}",
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build)));

        publisher.DrainDropped(projectId).Should().Be(1);
        publisher.DrainDropped(projectId).Should().Be(0);
    }

    [Fact]
    public async Task Publisher_bounds_long_redacted_output_with_an_explicit_marker()
    {
        var publisher = new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(new DeploymentDiagnosticOptions { BufferCapacity = 16 }),
            NullLogger<DeploymentDiagnosticPublisher>.Instance);
        await publisher.PublishAsync(new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(),
            DeploymentDiagnosticSource.DockerCompose, DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            new string('a', 5_000) + " password=secret",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build)));

        var queued = await publisher.Reader.ReadAsync();
        queued.Message.Should().Contain("[output truncated]").And.NotContain("secret");
        queued.Message.Length.Should().BeLessThan(4_200);
    }

    [Fact]
    public void Redact_masks_secrets_in_messages_and_attributes_before_delivery()
    {
        var diagnosticEvent = new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(),
            DeploymentDiagnosticSource.GitHubActions, DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            "access_token=very-secret Authorization: Bearer another-secret",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build),
            new Dictionary<string, string> { ["refresh_token"] = "also-secret", ["revision"] = "v1" });

        var result = new DiagnosticRedactor().Redact(diagnosticEvent);

        result.Event.Message.Should().NotContain("very-secret").And.NotContain("another-secret")
            .And.Contain("[REDACTED]");
        result.Event.Attributes!["refresh_token"].Should().Be("[REDACTED]");
        result.Event.Attributes["revision"].Should().Be("v1");
        result.RedactedValueCount.Should().Be(2);
    }

    [Fact]
    public void Redact_removes_untrusted_terminal_controls_but_keeps_progress_carriage_returns()
    {
        var diagnosticEvent = new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(),
            DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            "progress\rnext\u001b[2J\u001b]0;forged title\u0007done\n",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"));

        new DiagnosticRedactor().Redact(diagnosticEvent).Event.Message.Should().Be("progress\rnextdone\n");
    }

    [Fact]
    public void Event_contract_supports_typed_non_secret_source_identity()
    {
        var sourceIdentity = new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Job,
            DeploymentDiagnosticStream.StandardError, "12345");

        sourceIdentity.Component.Should().Be(DeploymentDiagnosticComponent.Job);
        sourceIdentity.Stream.Should().Be(DeploymentDiagnosticStream.StandardError);
        sourceIdentity.InstanceId.Should().Be("12345");
    }
}