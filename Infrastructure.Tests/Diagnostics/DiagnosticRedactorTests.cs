using Application.Abstractions.Diagnostics;
using Application.Diagnostics;
using FluentAssertions;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

public sealed class DiagnosticRedactorTests
{
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
    public void Event_contract_supports_typed_non_secret_source_identity()
    {
        var sourceIdentity = new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Job,
            DeploymentDiagnosticStream.StandardError, "12345");

        sourceIdentity.Component.Should().Be(DeploymentDiagnosticComponent.Job);
        sourceIdentity.Stream.Should().Be(DeploymentDiagnosticStream.StandardError);
        sourceIdentity.InstanceId.Should().Be("12345");
    }
}