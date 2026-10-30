using Domain.Entities.Ai;
using Domain.Entities.Diagnostics;
using Domain.Entities.Projects;
using Domain.Enums.Deployments;

namespace Domain.Entities.Deployments;

/// <summary>
///     Records one deployment launch, its runtime state, completion outcome and resolved
///     resource identity.
/// </summary>
public class Deployment : BaseEntity
{
    /// <summary>Last owner-saved assessment choices; never credentials or diagnostics.</summary>
    public string? AiAssessmentPreferencesJson { get; init; }

    /// <summary>Serialized immutable non-secret configuration; null for legacy deployments.</summary>
    public string? ConfigurationSnapshotJson { get; set; }

    /// <summary>Completion outcome retained after runtime status changes.</summary>
    public DeploymentOutcome Outcome { get; set; }

    /// <summary>Time a deployment completed preparation successfully or failed.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Resolved local host port; independent of later configuration edits.</summary>
    public int? ResolvedHostPort { get; set; }

    /// <summary>The unique identifier of the project associated with this deployment.</summary>
    public Guid CsProjectId { get; init; }

    /// <summary>A reference to the project associated with this deployment.</summary>
    public CsProject? CsProject { get; set; }

    /// <summary>Recorded runtime state; separate from the deployment preparation outcome.</summary>
    public DeploymentStatus Status { get; set; }

    /// <summary>The tag/name of the generated Docker image (e.g., "automate-mywebapi-local").</summary>
    public string? ImageTag { get; set; }


    /// <summary>Recorded Docker container identity, if one was resolved.</summary>
    public string? DockerContainerId { get; set; }


    /// <summary>The unique ID of the GitHub Actions workflow run associated with this deployment.</summary>
    public long? CloudGitHubActionRunId { get; set; }

    /// <summary>The URL of the deployed application in the cloud.</summary>
    public string? CloudAppUrl { get; set; }

    /// <summary>The revision of the deployed application in the cloud.</summary>
    public string? CloudContainerRevision { get; set; }

    /// <summary>Non-secret provider identity used to recover cloud collectors after restart.</summary>
    public string? CloudResourceId { get; set; }

    /// <summary>Recorded Azure Container App name used with the verified resource identity.</summary>
    public string? CloudContainerAppName { get; set; }

    /// <summary>Fencing identity of the current cloud runtime collector, if claimed.</summary>
    public Guid? RuntimeCollectorLeaseOwner { get; init; }

    /// <summary>UTC expiry after which another collector may recover the lease.</summary>
    public DateTimeOffset? RuntimeCollectorLeaseUntil { get; init; }

    /// <summary>Durable non-sensitive checkpoints for GitHub Actions diagnostic streaming.</summary>
    public ICollection<GitHubWorkflowCheckpoint> GitHubWorkflowCheckpoints { get; init; } = [];

    /// <summary>Durable non-sensitive cursors for Azure Container Apps runtime log streaming.</summary>
    public ICollection<AzureContainerAppLogCheckpoint> AzureContainerAppLogCheckpoints { get; init; } = [];

    /// <summary>Saved analysis requests and results associated with this deployment.</summary>
    public ICollection<AiDeploymentAnalysis> AiAnalyses { get; init; } = [];

    /// <summary>Related legacy diagnostic history and retained delivery-buffer records.</summary>
    public ICollection<DeploymentDiagnosticRecord> DiagnosticRecords { get; init; } = [];
}
