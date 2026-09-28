namespace Domain.Enums;

/// <summary>Records the most recent outcome of retrieving one GitHub Actions job log.</summary>
public enum GitHubWorkflowLogAvailability
{
    Unknown,
    Available,
    NotAvailable,
    AccessDenied,
    Failed
}
