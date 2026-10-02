namespace Domain.Enums;

/// <summary>Lifecycle state of an asynchronous AI deployment analysis.</summary>
public enum AiAnalysisStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Skipped
}