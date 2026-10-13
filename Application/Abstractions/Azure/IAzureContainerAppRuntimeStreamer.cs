using Domain.DTO;

namespace Application.Abstractions.Azure;

/// <summary>
///     Supervises host-managed Azure runtime log and metric collection for verified deployment
///     resources.
/// </summary>
public interface IAzureContainerAppRuntimeStreamer
{
    /// <summary>
    ///     Starts background streaming for a cloud deployment's Container App.
    /// </summary>
    /// <param name="request">The verified deployment resource and revision to supervise.</param>
    void StartStreaming(AzureContainerAppRuntimeStreamRequest request);
}
