using System.Collections.Concurrent;
using Core.DTO;
using Microsoft.Extensions.Logging;
using Services.LogStreaming;

namespace Services.Azure;

/// <summary>
///     Polls Azure Container Apps runtime state and metrics and publishes updates through SignalR.
/// </summary>
public sealed class AzureContainerAppRuntimeStreamer(
    ILogStreamer logStreamer,
    IHttpClientFactory httpClientFactory,
    ILogger<AzureContainerAppRuntimeStreamer> logger) : IAzureContainerAppRuntimeStreamer
{
    /// <summary>
    ///     Container name used when publishing cloud runtime updates to the existing log stream UI.
    /// </summary>
    private const string CloudWebContainerName = "cloud-web";

    /// <summary>
    ///     Delay between runtime state and metrics polling attempts.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Active per-project stream cancellation sources, keyed by project ID.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveStreams = new();

    /// <summary>
    ///     Lightweight ARM client used by the background polling loop.
    /// </summary>
    private readonly AzureContainerAppClient _containerAppClient = new(httpClientFactory);

    /// <inheritdoc />
    public void StartStreaming(AzureCloudCredentialsDto credentials, DeploymentConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(config);

        if (!TryCreateStreamTarget(credentials, config, out var target))
            return;

        var cts = new CancellationTokenSource();
        ActiveStreams.AddOrUpdate(config.ProjectId, cts, (_, oldCts) =>
        {
            oldCts.Cancel();
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamRuntimeAsync(target, cts.Token);
            }
            finally
            {
                RemoveActiveStream(target.ProjectId, cts);

                cts.Dispose();
            }
        }, cts.Token);
    }


    /// <summary>
    ///     Continuously polls the Azure Container App for its latest revision, FQDN, and resource metrics,
    ///     and streams updates to clients via SignalR.
    /// </summary>
    /// <param name="target">The Container App stream target.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task StreamRuntimeAsync(ContainerAppStreamTarget target, CancellationToken cancellationToken)
    {
        var lastRevision = string.Empty;
        var lastFqdn = string.Empty;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = await _containerAppClient.GetStateAsync(target.ResourceId, target.AccessToken,
                    cancellationToken);
                if (state != null && (state.LatestRevision != lastRevision || state.Fqdn != lastFqdn))
                {
                    lastRevision = state.LatestRevision;
                    lastFqdn = state.Fqdn;

                    await logStreamer.StreamContainerLogsAsync(target.ProjectId, CloudWebContainerName,
                        CreateAvailabilityMessage(state));
                }

                var metrics = await _containerAppClient.GetMetricsAsync(target.ResourceId, target.AccessToken,
                    cancellationToken);
                if (metrics != null)
                    await logStreamer.StreamContainerMetricsAsync(target.ProjectId, CloudWebContainerName, metrics.Cpu,
                        metrics.Memory);

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[AzureContainerAppRuntimeStreamer] Runtime streaming cancelled for Project ID {Id}.",
                target.ProjectId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[AzureContainerAppRuntimeStreamer] Error while streaming Azure Container Apps runtime data for Project ID {Id}.",
                target.ProjectId);
        }
    }


    /// <summary>
    ///     Constructs the Azure resource ID for the container app based on the
    ///     subscription ID, resource group name, and container app name.
    /// </summary>
    /// <param name="subscriptionId">The ID of the subscription.</param>
    /// <param name="resourceGroupName">The name of the resource group.</param>
    /// <param name="containerAppName">The name of the container app.</param>
    /// <returns>The resource ID of the container app.</returns>
    private static string BuildContainerAppResourceId(string subscriptionId, string resourceGroupName,
        string containerAppName)
    {
        return
            $"/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(containerAppName)}";
    }

    /// <summary>
    ///     Validates cloud runtime configuration and creates an immutable stream target.
    /// </summary>
    private static bool TryCreateStreamTarget(AzureCloudCredentialsDto credentials, DeploymentConfigDto config,
        out ContainerAppStreamTarget target)
    {
        target = default;

        if (string.IsNullOrWhiteSpace(credentials.AccessToken) ||
            string.IsNullOrWhiteSpace(credentials.SubscriptionId) ||
            string.IsNullOrWhiteSpace(config.CloudResourceGroupName) ||
            string.IsNullOrWhiteSpace(config.CloudContainerAppName))
            return false;

        target = new ContainerAppStreamTarget(
            config.ProjectId,
            BuildContainerAppResourceId(credentials.SubscriptionId, config.CloudResourceGroupName,
                config.CloudContainerAppName),
            credentials.AccessToken);

        return true;
    }

    /// <summary>
    ///     Removes a stream registration only when it still belongs to the completing worker.
    /// </summary>
    private static void RemoveActiveStream(Guid projectId, CancellationTokenSource streamCancellation)
    {
        if (ActiveStreams.TryGetValue(projectId, out var activeCts) && ReferenceEquals(activeCts, streamCancellation))
            ActiveStreams.TryRemove(projectId, out _);
    }

    /// <summary>
    ///     Creates a human-readable availability line for the terminal-style deployment log.
    /// </summary>
    private static string CreateAvailabilityMessage(AzureContainerAppState state)
    {
        return
            $"Azure Container App is available{(string.IsNullOrWhiteSpace(state.Fqdn) ? string.Empty : $" at https://{state.Fqdn}")}. Latest ready revision: {state.LatestRevision}.";
    }

    /// <summary>
    ///     Immutable target data needed by one Azure runtime polling worker.
    /// </summary>
    private readonly record struct ContainerAppStreamTarget(Guid ProjectId, string ResourceId, string AccessToken);
}