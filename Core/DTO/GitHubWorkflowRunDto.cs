namespace Core.DTO;

/// <summary>
///     Represents a GitHub Actions workflow run tracked by AutoMate.
/// </summary>
public record GitHubWorkflowRunDto
{
    /// <summary>
    ///     The GitHub workflow run identifier.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    ///     The workflow status reported by GitHub, such as queued, in_progress, or completed.
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    ///     The workflow conclusion reported by GitHub when the run has completed.
    /// </summary>
    public string? Conclusion { get; init; }

    /// <summary>
    ///     The workflow run URL in GitHub.
    /// </summary>
    public string HtmlUrl { get; init; } = string.Empty;

    /// <summary>
    ///     The commit SHA associated with the workflow run.
    /// </summary>
    public string HeadSha { get; init; } = string.Empty;

    /// <summary>
    ///     The branch associated with the workflow run.
    /// </summary>
    public string HeadBranch { get; init; } = string.Empty;
}