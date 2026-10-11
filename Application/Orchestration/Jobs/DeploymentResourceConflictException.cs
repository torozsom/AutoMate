namespace Application.Orchestration;

/// <summary>A safe, actionable local resource conflict that can be shown in deployment output.</summary>
public sealed class DeploymentResourceConflictException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);