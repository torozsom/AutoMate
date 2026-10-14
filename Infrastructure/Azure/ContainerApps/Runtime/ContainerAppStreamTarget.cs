using Domain.DTO.Azure;
using Infrastructure.Azure.Databases;

namespace Infrastructure.Azure.ContainerApps.Runtime;

/// <summary>
///     Holds verified resource identity, collector cursors and current runtime observations.
/// </summary>
/// <param name="projectId">The project identity used for authorization or correlation.</param>
/// <param name="deploymentId">
///     The deployment identity; null is used only where legacy observations permit it.
/// </param>
/// <param name="userId">The internal AutoMate user identity.</param>
/// <param name="resourceId">Verified Azure resource identity used to scope provider reads.</param>
/// <param name="containerAppName">Verified Azure Container App name.</param>
/// <param name="azureCredentials">
///     Resolves current scoped Azure authorization without logging credentials.
/// </param>
internal sealed class ContainerAppStreamTarget(
    Guid projectId,
    Guid deploymentId,
    Guid userId,
    string resourceId,
    string containerAppName,
    AzureCloudCredentialsDto azureCredentials)
{
    /// <summary>Only the latest verified active deployment collects shared database observations.</summary>
    public bool CollectDatabases { get; set; }

    /// <summary>
    ///     Configured exact database container names; managed databases use their separate resource
    ///     collector.
    /// </summary>
    public IReadOnlySet<string> DatabaseContainers { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Metric sampling can be slower than the sixty-second live ownership heartbeat.</summary>
    public DateTimeOffset NextMetricsAt { get; set; }

    /// <summary>
    ///     Each resource uses the configured sampling interval despite more frequent lease renewal.
    /// </summary>
    public bool MetricsDue { get; set; }

    /// <summary>Prevents overlapping metric polling windows from printing the same minute repeatedly.</summary>
    public Dictionary<string, DateTimeOffset> LastMetricSummary { get; } = new(StringComparer.Ordinal);

    /// <summary>Verified collector project id metadata.</summary>
    public Guid ProjectId { get; } = projectId;

    /// <summary>Verified collector deployment id metadata.</summary>
    public Guid DeploymentId { get; } = deploymentId;

    /// <summary>Verified collector user id metadata.</summary>
    public Guid UserId { get; } = userId;

    /// <summary>Verified collector resource id metadata.</summary>
    public string ResourceId { get; } = resourceId;

    /// <summary>Environment ARM scope resolved from the selected app, refreshed with state reads.</summary>
    public string? LogResourceId { get; set; }

    /// <summary>Verified collector log environment name metadata.</summary>
    public string? LogEnvironmentName { get; set; }

    /// <summary>Verified collector log environment resource name metadata.</summary>
    public string? LogEnvironmentResourceName { get; set; }

    /// <summary>Verified tagged inventory retained only in memory between metrics and log polling.</summary>
    public IReadOnlyList<AzureManagedDatabase> Databases { get; set; } = [];

    /// <summary>Recorded provider composition, or null for legacy snapshots lacking an inventory.</summary>
    public IReadOnlySet<string>? DatabaseProviders { get; set; }

    /// <summary>Verified collector container app name metadata.</summary>
    public string ContainerAppName { get; } = containerAppName;

    /// <summary>Verified collector azure credentials metadata.</summary>
    public AzureCloudCredentialsDto AzureCredentials { get; set; } = azureCredentials;

    /// <summary>Verified collector expected revision metadata.</summary>
    public string? ExpectedRevision { get; set; }

    /// <summary>Verified collector last revision metadata.</summary>
    public string LastRevision { get; set; } = string.Empty;

    /// <summary>Verified collector last fqdn metadata.</summary>
    public string LastFqdn { get; set; } = string.Empty;
}
