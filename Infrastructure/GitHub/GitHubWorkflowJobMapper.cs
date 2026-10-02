using Domain.DTO;

namespace Infrastructure.GitHub;

/// <summary>Maps GitHub REST job payloads into Application-facing DTOs.</summary>
internal static class GitHubWorkflowJobMapper
{
    /// <summary>Projects GitHub job and step API fields into provider-neutral progress DTOs.</summary>
    public static IReadOnlyList<GitHubWorkflowJobDto> Map(IEnumerable<GitHubWorkflowJobItem> jobs)
    {
        return jobs.Select(job => new GitHubWorkflowJobDto
        {
            Id = job.Id,
            Name = job.Name,
            Status = job.Status,
            Conclusion = job.Conclusion,
            HtmlUrl = job.HtmlUrl,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Steps = (job.Steps ?? [])
                .Select(step => new GitHubWorkflowStepDto
                {
                    Number = step.Number,
                    Name = step.Name,
                    Status = step.Status,
                    Conclusion = step.Conclusion
                })
                .ToArray()
        }).ToArray();
    }
}