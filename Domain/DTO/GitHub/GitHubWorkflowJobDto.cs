using Domain.Enums;

namespace Domain.DTO;

/// <summary>
///     Provider-neutral projection of a GitHub Actions workflow job.
/// </summary>
public sealed record GitHubWorkflowJobDto
{
    /// <summary>GitHub's stable job identifier within a workflow run.</summary>
    public long Id { get; init; }

    /// <summary>Display name assigned to the workflow job.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Current job state, such as queued, in_progress, or completed.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Terminal outcome when the job has completed.</summary>
    public string? Conclusion { get; init; }

    /// <summary>GitHub URL for inspecting the job.</summary>
    public string HtmlUrl { get; init; } = string.Empty;

    /// <summary>Time the job began, when reported by GitHub.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Time the job finished, when reported by GitHub.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Ordered steps used for terminal progress reporting.</summary>
    public IReadOnlyList<GitHubWorkflowStepDto> Steps { get; init; } = [];
}

/// <summary>
///     Provider-neutral projection of a GitHub Actions job step.
/// </summary>
public sealed record GitHubWorkflowStepDto
{
    /// <summary>Step number within its job.</summary>
    public int Number { get; init; }

    /// <summary>Display name assigned to the step.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Current step state reported by GitHub.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Terminal outcome when the step has completed.</summary>
    public string? Conclusion { get; init; }
}

/// <summary>
///     Job-log retrieval outcome with optional untrusted provider output.
/// </summary>
/// <param name="Availability">The finite result of attempting to retrieve the job log.</param>
/// <param name="Content">Untrusted provider log content, or null when no content was retrieved.</param>
public sealed record GitHubWorkflowJobLogDownload(
    GitHubWorkflowLogAvailability Availability,
    string? Content = null);

/// <summary>
///     One entry from the final workflow archive; its content requires normalization and
///     redaction.
/// </summary>
/// <param name="Path">The workflow archive entry path used to match job output.</param>
/// <param name="Content">Untrusted text read from the completed workflow archive entry.</param>
public sealed record GitHubWorkflowLogArchiveEntryDto(string Path, string Content);