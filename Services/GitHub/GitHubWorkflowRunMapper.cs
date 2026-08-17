using Core.DTO;

namespace Services.GitHub;

/// <summary>
///     Selects and maps GitHub workflow run API items into AutoMate DTOs.
/// </summary>
internal static class GitHubWorkflowRunMapper
{
    /// <summary>
    ///     Selects the latest run matching the optional commit SHA and preferred workflow file.
    /// </summary>
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
    private static GitHubWorkflowRunDto Map(GitHubWorkflowRunItem run)
    {
        return new GitHubWorkflowRunDto
        {
            Id = run.Id,
            Status = run.Status,
            Conclusion = run.Conclusion,
            HtmlUrl = run.HtmlUrl,
            HeadSha = run.HeadSha,
            HeadBranch = run.HeadBranch
        };
    }
}