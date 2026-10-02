using System.Text.Json.Serialization;

namespace Infrastructure.GitHub;

/// <summary>
///     Response shape returned by GitHub when listing workflow runs.
/// </summary>
/// <param name="WorkflowRuns">The workflow run items returned by GitHub.</param>
internal sealed record GitHubWorkflowRunsResponse(
    [property: JsonPropertyName("workflow_runs")]
    List<GitHubWorkflowRunItem> WorkflowRuns);

/// <summary>
///     GitHub workflow run item fields consumed by AutoMate.
/// </summary>
/// <param name="Id">GitHub workflow run identifier.</param>
/// <param name="Attempt">Attempt number for reruns.</param>
/// <param name="Status">Current run state.</param>
/// <param name="Conclusion">Terminal outcome, when available.</param>
/// <param name="HtmlUrl">GitHub run details URL.</param>
/// <param name="HeadSha">Commit that triggered the run.</param>
/// <param name="HeadBranch">Branch that triggered the run.</param>
/// <param name="Path">Workflow file path.</param>
/// <param name="CreatedAt">Run creation time used to select the latest match.</param>
internal sealed record GitHubWorkflowRunItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("run_attempt")]
    int Attempt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")]
    string? Conclusion,
    [property: JsonPropertyName("html_url")]
    string HtmlUrl,
    [property: JsonPropertyName("head_sha")]
    string HeadSha,
    [property: JsonPropertyName("head_branch")]
    string HeadBranch,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("created_at")]
    DateTimeOffset CreatedAt);