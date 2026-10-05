using Microsoft.Extensions.Logging;

namespace Application.Diagnostics;

/// <summary>Fixed audit operations; diagnostic text, identities and provider payloads are never event properties.</summary>
public enum AuditOperation
{
    /// <summary>Local or external authentication.</summary>
    Authentication = 0,

    /// <summary>Endpoint authorization.</summary>
    Authorization = 1,

    /// <summary>Request rate limiting.</summary>
    RateLimit = 2,

    /// <summary>Analysis background processing.</summary>
    Analysis = 3,

    /// <summary>Analysis provider execution and result validation.</summary>
    AnalysisProvider = 4,

    /// <summary>Expired analysis cleanup.</summary>
    AnalysisRetention = 5
}

/// <summary>Finite outcomes suitable for searching operational logs.</summary>
public enum AuditOutcome
{
    /// <summary>An operation began.</summary>
    Started,

    /// <summary>An operation completed successfully.</summary>
    Completed,

    /// <summary>An operation failed without exposing exception details.</summary>
    Failed,

    /// <summary>The request was refused.</summary>
    Denied,

    /// <summary>Authentication is required.</summary>
    Challenged,

    /// <summary>The operation could not run under current configuration/data availability.</summary>
    Unavailable,

    /// <summary>Untrusted results did not pass validation.</summary>
    InvalidResult,

    /// <summary>Caller cancellation interrupted processing.</summary>
    Canceled,

    /// <summary>Expired/deleted work was discarded.</summary>
    Discarded,

    /// <summary>An external authentication ticket was prepared; final cookie sign-in has not yet completed.</summary>
    Prepared,

    /// <summary>A transient provider failure was durably scheduled for a later consent-checked attempt.</summary>
    RetryScheduled,

    /// <summary>The owner durably canceled an analysis; this user action is recorded at Information.</summary>
    CanceledByOwner
}

/// <summary>Emits fixed structured audit events and GUID-only correlation scopes through ILogger.</summary>
public static class OperationalLog
{
    /// <summary>Attaches correlation metadata, never names, paths, users, credentials or diagnostic payloads.</summary>
    public static IDisposable? BeginCorrelation(ILogger logger, Guid? deploymentId = null, Guid? analysisId = null,
        Guid? projectId = null)
    {
        var attributes = new Dictionary<string, object?>();
        if (deploymentId is not null) attributes["DeploymentId"] = deploymentId.Value;
        if (analysisId is not null) attributes["AnalysisId"] = analysisId.Value;
        if (projectId is not null) attributes["ProjectId"] = projectId.Value;
        return logger.BeginScope(attributes);
    }

    /// <summary>Records only allowlisted operation/outcome names with stable event IDs and no exception objects.</summary>
    public static void Record(ILogger logger, AuditOperation operation, AuditOutcome outcome)
    {
        var operationName = Enum.GetName(operation) ?? "Unknown";
        var outcomeName = Enum.GetName(outcome) ?? "Unknown";
        var level = outcome switch
        {
            AuditOutcome.Failed or AuditOutcome.Denied or AuditOutcome.Challenged or AuditOutcome.InvalidResult =>
                LogLevel.Warning,
            AuditOutcome.Discarded => LogLevel.Debug,
            _ => LogLevel.Information
        };
        logger.Log(level, new EventId(1000 + (Enum.IsDefined(operation) ? (int)operation : 99), operationName),
            "AutoMate operation {Operation} outcome {Outcome}.", operationName, outcomeName);
    }
}
