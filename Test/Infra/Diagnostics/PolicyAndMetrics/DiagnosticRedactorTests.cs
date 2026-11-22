using System.Text.Json;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Diagnostics;
using FluentAssertions;
using Infrastructure.Diagnostics.Delivery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Infra.Diagnostics.PolicyAndMetrics;

/// <summary>
///     Verifies credential masking, terminal-control removal and bounded diagnostic metadata.
/// </summary>
public sealed class DiagnosticRedactorTests
{
    /// <summary>
    ///     Every string field is sanitized; GUIDs, numeric samples, ordering and legal routing remain
    ///     intact.
    /// </summary>
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


    /// <summary>
    ///     Verifies that publisher redacts before placing an event on the delivery queue.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Verifies that publisher accepts a blank git hub log line.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Verifies that publisher reports bounded queue overflow for a durable gap marker.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Verifies that publisher bounds long redacted output with an explicit marker.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Verifies that redact masks secrets in messages and attributes before delivery.
    /// </summary>
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

    /// <summary>
    ///     Verifies that redact removes untrusted terminal controls but keeps progress carriage
    ///     returns.
    /// </summary>
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

    /// <summary>
    ///     Verifies that event contract supports typed non secret source identity.
    /// </summary>
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
