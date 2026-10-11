namespace Domain.Enums;

/// <summary>Records the most recent outcome of retrieving one GitHub Actions job log.</summary>
public enum GitHubWorkflowLogAvailability
{
    /// <summary>No download has been attempted.</summary>
    Unknown,

    /// <summary>GitHub returned a downloadable job log.</summary>
    Available,

    /// <summary>GitHub has not made the requested log available.</summary>
    NotAvailable,

    /// <summary>The token lacks permission to retrieve the log.</summary>
    AccessDenied,

    /// <summary>The request failed for a reason other than availability or permission.</summary>
    Failed
}