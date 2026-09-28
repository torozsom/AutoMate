using Domain.Enums;

namespace Domain.DTO;

/// <summary>Provider-neutral projection of a GitHub Actions workflow job.</summary>
public sealed record GitHubWorkflowJobDto
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Conclusion { get; init; }
    public string HtmlUrl { get; init; } = string.Empty;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<GitHubWorkflowStepDto> Steps { get; init; } = [];
}

/// <summary>Provider-neutral projection of a GitHub Actions job step.</summary>
public sealed record GitHubWorkflowStepDto
{
    public int Number { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Conclusion { get; init; }
}

/// <summary>Represents one safe-to-handle result from a GitHub job-log request.</summary>
public sealed record GitHubWorkflowJobLogDownload(
    GitHubWorkflowLogAvailability Availability,
    string? Content = null);

/// <summary>One entry from GitHub's final workflow log archive.</summary>
public sealed record GitHubWorkflowLogArchiveEntryDto(string Path, string Content);
