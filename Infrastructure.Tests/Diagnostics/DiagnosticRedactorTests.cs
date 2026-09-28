using Application.Abstractions.Diagnostics;
using FluentAssertions;
using Infrastructure.Diagnostics;
using Application.Diagnostics;
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
}
