using Core.DTO;

namespace Services.Azure;

/// <summary>
///     Streams Azure Container Apps runtime telemetry into AutoMate's real-time log pipeline.
/// </summary>
public interface IAzureContainerAppRuntimeStreamer
{
    /// <summary>
    ///     Starts background streaming for a cloud deployment's Container App.
    /// </summary>
    void StartStreaming(AzureCloudCredentialsDto credentials, DeploymentConfigDto config);
}