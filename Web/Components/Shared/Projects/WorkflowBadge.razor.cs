using Domain.Enums.Deployments;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared.Projects;

/// <summary>
///     Presents remote workflow progress with an optional GitHub Actions link.
/// </summary>
public partial class WorkflowBadge
{
    /// <summary>Deployment status to present, if available.</summary>
    [Parameter]
    public DeploymentStatus? Status { get; set; }

    /// <summary>Optional workflow progress message.</summary>
    [Parameter]
    public string? Message { get; set; }

    /// <summary>Optional GitHub Actions workflow destination.</summary>
    [Parameter]
    public string? WorkflowUrl { get; set; }

    /// <summary>Explicit workflow message or status-derived fallback.</summary>
    private string DisplayMessage => !string.IsNullOrWhiteSpace(Message)
        ? Message
        : Status switch
        {
            DeploymentStatus.Starting => "Workflow setup is starting.",
            DeploymentStatus.Running => "Workflow is queued, running, or completed successfully.",
            DeploymentStatus.Failed => "Workflow setup or execution failed.",
            DeploymentStatus.Stopped => "Deployment has been stopped.",
            _ => "No cloud workflow has been started yet."
        };

    /// <summary>Status-dependent workflow alert class.</summary>
    private string AlertClass => Status switch
    {
        DeploymentStatus.Running => "workflow-banner-success",
        DeploymentStatus.Starting => "workflow-banner-info",
        DeploymentStatus.Failed => "workflow-banner-danger",
        DeploymentStatus.Stopped => "workflow-banner-muted",
        _ => "workflow-banner-neutral"
    };

    /// <summary>Status-dependent icon CSS classes.</summary>
    private string IconClass => Status switch
    {
        DeploymentStatus.Running => "bi-check-circle-fill",
        DeploymentStatus.Starting => "bi-hourglass-split",
        DeploymentStatus.Failed => "bi-exclamation-triangle-fill",
        DeploymentStatus.Stopped => "bi-stop-circle-fill",
        _ => "bi-github"
    };
}
