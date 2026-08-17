using System.Text.Json.Serialization;

namespace Services.GitHub;

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
internal sealed record GitHubWorkflowRunItem(
    [property: JsonPropertyName("id")] long Id,
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