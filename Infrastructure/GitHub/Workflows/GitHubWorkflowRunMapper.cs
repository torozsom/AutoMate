using Domain.DTO;

namespace Infrastructure.GitHub;

/// <summary>
///     Selects and maps GitHub workflow run API items into AutoMate DTOs.
/// </summary>
internal static class GitHubWorkflowRunMapper
{
    /// <summary>
    ///     Selects the latest run matching the optional commit SHA and preferred workflow file.
    /// </summary>
    /// <param name="workflowRuns">Provider workflow-run projections to normalize.</param>
    /// <param name="workflowFileName">The workflow filename selected for dispatch or lookup.</param>
    /// <param name="headSha">The optional commit identity used to restrict workflow-run lookup.</param>
    /// <returns>The normalized verified workflow-run metadata, or null when no value is available.</returns>
    public static GitHubWorkflowRunDto? SelectLatestMatchingRun(
        IEnumerable<GitHubWorkflowRunItem> workflowRuns,
        string workflowFileName,
        string? headSha)
    {
        var matchingRuns = workflowRuns
            .Where(r => string.IsNullOrWhiteSpace(headSha) ||
                        string.Equals(r.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var workflowFilePathSuffix = $"/{workflowFileName}";
        var preferredRuns = matchingRuns
            .Where(r => string.IsNullOrWhiteSpace(workflowFileName) ||
                        r.Path?.EndsWith(workflowFilePathSuffix, StringComparison.OrdinalIgnoreCase) == true ||
                        string.Equals(r.Path, workflowFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (preferredRuns.Count == 0)
            preferredRuns = matchingRuns;

        return preferredRuns
            .OrderByDescending(r => r.CreatedAt)
            .Select(Map)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Maps one GitHub workflow run API item to the public DTO shape.
    /// </summary>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <returns>The normalized verified workflow-run metadata.</returns>
    private static GitHubWorkflowRunDto Map(GitHubWorkflowRunItem run)
    {
        return new GitHubWorkflowRunDto
        {
            Id = run.Id,
            Attempt = Math.Max(1, run.Attempt),
            Status = run.Status,
            Conclusion = run.Conclusion,
            HtmlUrl = run.HtmlUrl,
            HeadSha = run.HeadSha,
            HeadBranch = run.HeadBranch
        };
    }
}
