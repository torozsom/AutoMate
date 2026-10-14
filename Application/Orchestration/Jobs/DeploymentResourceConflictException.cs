namespace Application.Orchestration.Jobs;

/// <summary>
///     A safe, actionable local resource conflict that can be shown in deployment output.
/// </summary>
/// <param name="message">The message supplied to the operation.</param>
/// <param name="innerException">The original failure, retained only as exception context.</param>
public sealed class DeploymentResourceConflictException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
