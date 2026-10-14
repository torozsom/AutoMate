using Application.Abstractions.Diagnostics.Events;

namespace Application.Abstractions.Diagnostics.Redaction;

/// <summary>
///     Redacts externally sourced diagnostic text and attributes before they reach any sink.
/// </summary>
public interface IDiagnosticRedactor
{
    /// <summary>
    ///     Returns a safe copy of the supplied event and the number of changed fields.
    /// </summary>
    /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
    /// <returns>A safe diagnostic copy and the count of redacted values.</returns>
    RedactionResult Redact(DeploymentDiagnosticEvent diagnosticEvent);

    /// <summary>
    ///     Redacts and bounds standalone transport/context text; oversized inputs fail closed.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <param name="maximumCharacters">The maximum permitted text length.</param>
    /// <returns>Bounded text with supported sensitive values and unsafe control characters removed.</returns>
    string RedactText(string value, int maximumCharacters = 4096);
}

/// <summary>
///     Result of diagnostic redaction.
/// </summary>
/// <param name="Event">The normalized diagnostic observation.</param>
/// <param name="RedactedValueCount">The number of values masked by redaction.</param>
public sealed record RedactionResult(DeploymentDiagnosticEvent Event, int RedactedValueCount);
