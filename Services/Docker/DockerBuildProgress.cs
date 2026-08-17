using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace Services.Docker;

/// <summary>
///     Tracks Docker image build progress and records whether Docker reported a build error.
/// </summary>
internal sealed class DockerBuildProgress(ILogger logger)
{
    /// <summary>
    ///     Indicates whether any Docker build progress message contained an error.
    /// </summary>
    public bool HasError { get; private set; }

    /// <summary>
    ///     Handles one Docker build progress message from Docker.DotNet.
    /// </summary>
    public void Handle(JSONMessage message)
    {
        if (!string.IsNullOrEmpty(message.Stream))
        {
            logger.LogDebug("[DockerService] {Message}", message.Stream.TrimEnd());
        }
        else if (!string.IsNullOrEmpty(message.Status))
        {
            if (!string.IsNullOrEmpty(message.ProgressMessage))
                logger.LogDebug("[DockerService] {Status} {Progress}", message.Status, message.ProgressMessage);
            else
                logger.LogDebug("[DockerService] {Status}", message.Status);
        }

        if (string.IsNullOrEmpty(message.ErrorMessage))
            return;

        logger.LogError("[DOCKER BUILD ERROR]: {ErrorMessage}", message.ErrorMessage);
        HasError = true;
    }
}