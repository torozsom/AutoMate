using Application.Abstractions.Diagnostics;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Docker;

/// <summary>
///     Tracks Docker image build progress and records whether Docker reported a build error.
/// </summary>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
internal sealed class DockerBuildProgress(ILogger logger, IDiagnosticRedactor redactor) : IProgress<JSONMessage>
{
    /// <summary>Indicates whether any Docker build progress message contained an error.</summary>
    public bool HasError { get; private set; }

    /// <summary>
    ///     Handles one Docker build progress message from Docker.DotNet.
    /// </summary>
    /// <param name="message">The message supplied to the operation.</param>
    public void Report(JSONMessage message)
    {
        if (!string.IsNullOrEmpty(message.Stream))
        {
            logger.LogDebug("[DockerService] {Message}", Safe(message.Stream.TrimEnd()));
        }
        else if (!string.IsNullOrEmpty(message.Status))
        {
            if (!string.IsNullOrEmpty(message.ProgressMessage))
                logger.LogDebug("[DockerService] {Status} {Progress}", Safe(message.Status),
                    Safe(message.ProgressMessage));
            else
                logger.LogDebug("[DockerService] {Status}", Safe(message.Status));
        }

        if (string.IsNullOrEmpty(message.ErrorMessage))
            return;

        logger.LogError("[DOCKER BUILD ERROR]: {ErrorMessage}", Safe(message.ErrorMessage));
        HasError = true;
    }

    /// <summary>
    ///     Redacts SDK text before it reaches the host logger.
    /// </summary>
    /// <param name="text">The diagnostic context or text being processed.</param>
    /// <returns>The bounded normalized text with supported unsafe values removed.</returns>
    private string Safe(string text)
    {
        return redactor.Redact(new DeploymentDiagnosticEvent(Guid.Empty, null,
            DeploymentDiagnosticSource.DockerCompose, DeploymentDiagnosticKind.BuildProgress,
            DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, text,
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build))).Event.Message;
    }
}