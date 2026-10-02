using System.Text.Json.Serialization;

namespace Infrastructure.GitHub;

/// <summary>GitHub REST response for a workflow run's jobs.</summary>
/// <param name="Jobs">Jobs returned by the requested page.</param>
internal sealed record GitHubWorkflowJobsResponse(
    [property: JsonPropertyName("jobs")] List<GitHubWorkflowJobItem> Jobs);

/// <summary>GitHub REST fields used to monitor one workflow job.</summary>
/// <param name="Id">Stable GitHub job identifier.</param>
/// <param name="Name">Job display name.</param>
/// <param name="Status">Current execution state.</param>
/// <param name="Conclusion">Outcome after completion.</param>
/// <param name="HtmlUrl">GitHub job details URL.</param>
/// <param name="StartedAt">Reported start time.</param>
/// <param name="CompletedAt">Reported completion time.</param>
/// <param name="Steps">Ordered steps included in the job response.</param>
internal sealed record GitHubWorkflowJobItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")]
    string? Conclusion,
    [property: JsonPropertyName("html_url")]
    string HtmlUrl,
    [property: JsonPropertyName("started_at")]
    DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completed_at")]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("steps")] List<GitHubWorkflowStepItem>? Steps);

/// <summary>GitHub REST fields used to monitor one workflow step.</summary>
/// <param name="Number">Step number within the job.</param>
/// <param name="Name">Step display name.</param>
/// <param name="Status">Current execution state.</param>
/// <param name="Conclusion">Outcome after completion.</param>
internal sealed record GitHubWorkflowStepItem(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")]
    string? Conclusion);