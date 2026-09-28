using System.Text.Json.Serialization;

namespace Infrastructure.GitHub;

/// <summary>GitHub REST response for a workflow run's jobs.</summary>
internal sealed record GitHubWorkflowJobsResponse(
    [property: JsonPropertyName("jobs")] List<GitHubWorkflowJobItem> Jobs);

/// <summary>GitHub REST fields used to monitor one workflow job.</summary>
internal sealed record GitHubWorkflowJobItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("steps")] List<GitHubWorkflowStepItem>? Steps);

/// <summary>GitHub REST fields used to monitor one workflow step.</summary>
internal sealed record GitHubWorkflowStepItem(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion);
