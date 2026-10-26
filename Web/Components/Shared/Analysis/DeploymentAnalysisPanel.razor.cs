using Application.Abstractions.Ai.Execution;
using Application.Ai.Policy;
using Application.Ai.Results;
using Domain.Enums.Ai;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared;

/// <summary>
///     Presents saved analysis state, fixed failure guidance and validated answer content.
/// </summary>
public partial class DeploymentAnalysisPanel
{
    /// <summary>Unique accessible heading association when multiple panels are rendered.</summary>
    private readonly string _headingId = "analysis-" + Guid.NewGuid().ToString("N");

    /// <summary>Saved-run presentation hides consent edits and execution controls.</summary>
    [Parameter]
    public bool ReadOnly { get; set; }

    /// <summary>Actual current deployment identity; stale results are omitted.</summary>
    [Parameter]
    public Guid? DeploymentId { get; set; }

    /// <summary>Owner-authorized safe result supplied by the Application use case.</summary>
    [Parameter]
    public DeploymentAnalysisView? Result { get; set; }

    /// <summary>Operator AI enablement; never implies provider approval.</summary>
    [Parameter]
    public bool Enabled { get; set; }

    /// <summary>Consent on the actual deployment's project configuration.</summary>
    [Parameter]
    public bool Consented { get; set; }

    /// <summary>Whether the current deployment has an exact eligible project whose consent can be edited.</summary>
    [Parameter]
    public bool CanEditConsent { get; set; }

    /// <summary>Whether the current deployment has persisted Failed status.</summary>
    [Parameter]
    public bool FailedDeployment { get; set; }

    /// <summary>Allows an operational assessment of any persisted deployment status.</summary>
    [Parameter]
    public bool AllowAnyStatus { get; set; }

    /// <summary>Disables duplicate owner actions while a request or refresh is pending.</summary>
    [Parameter]
    public bool Busy { get; set; }

    /// <summary>Authored action feedback, rendered as encoded text.</summary>
    [Parameter]
    public string? Message { get; set; }

    /// <summary>Owner request callback; the panel does not access persistence or provider services.</summary>
    [Parameter]
    public EventCallback OnRequest { get; set; }

    /// <summary>Owner refresh callback; persisted state remains authoritative.</summary>
    [Parameter]
    public EventCallback OnRefresh { get; set; }

    /// <summary>Explicit owner consent change; no provider is called by the panel.</summary>
    [Parameter]
    public EventCallback<bool> OnConsentChange { get; set; }

    /// <summary>Owner cancellation of the current queued/running analysis.</summary>
    [Parameter]
    public EventCallback OnCancel { get; set; }

    /// <summary>Rejects a response belonging to an older deployment selection.</summary>
    private DeploymentAnalysisView? CurrentResult => Result?.DeploymentId == DeploymentId ? Result : null;

    /// <summary>Matches UI eligibility to existing owner admission gates without replacing server authorization.</summary>
    private bool CanRequest => !Busy && Enabled && Consented && (FailedDeployment || AllowAnyStatus) &&
                               DeploymentId is not null &&
                               CurrentResult?.Status is not (AiAnalysisStatus.Queued or AiAnalysisStatus.Running);

    /// <summary>Textual status conveys meaning without depending on color.</summary>
    private string StatusLabel => CurrentResult?.Status switch
    {
        AiAnalysisStatus.Queued => "Queued", AiAnalysisStatus.Running => "Processing",
        AiAnalysisStatus.Completed => "Completed", AiAnalysisStatus.Cancelled => "Canceled",
        AiAnalysisStatus.Skipped => "Skipped", AiAnalysisStatus.Failed => "Analysis failed",
        _ => "No analysis yet"
    };

    /// <summary>Fixed state and eligibility guidance; raw failure codes and nonterminal payloads are never rendered.</summary>
    private string Explanation => CurrentResult?.Status switch
    {
        AiAnalysisStatus.Skipped => AnalysisSkipPolicy.Message(CurrentResult.FailureCode),
        AiAnalysisStatus.Cancelled => "The owner canceled this analysis.",
        AiAnalysisStatus.Failed when CurrentResult.FailureCode == AnalysisProviderAccessDeniedException.FailureCode =>
            AnalysisProviderAccessDeniedException.Guidance,
        AiAnalysisStatus.Failed when InvalidAnalysisResultException.Guidance(CurrentResult.FailureCode) is
            { } guidance => guidance,
        AiAnalysisStatus.Failed => "No complete result is available. You can request another analysis when enabled.",

        _ when Busy => "Updating analysis…",
        _ when !Enabled => "New AI analysis is disabled by the operator. Saved results remain available.",
        _ when !Consented => "Diagnostic data egress consent is not enabled for this deployment's project.",
        _ when !FailedDeployment && !AllowAnyStatus => "Analysis requests are available after a deployment fails.",

        AiAnalysisStatus.Queued or AiAnalysisStatus.Running =>
            "Status refreshes automatically every five seconds. You can also use Refresh analysis.",
        _ => "Analysis uses diagnostic data approved for this project."
    };

    /// <summary>
    ///     Explicit UTC timestamps prevent ambiguous deployment timing.
    /// </summary>
    /// <param name="value">UTC time to format.</param>
    /// <returns>The timestamp converted to UTC with an explicit UTC suffix.</returns>
    private static string Timestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");
    }
}
