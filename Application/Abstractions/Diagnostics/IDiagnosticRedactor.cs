namespace Application.Abstractions.Diagnostics;

/// <summary>Redacts externally sourced diagnostic text and attributes before they reach any sink.</summary>
public interface IDiagnosticRedactor
{
    /// <summary>Returns a safe copy of the supplied event and the number of changed fields.</summary>
    RedactionResult Redact(DeploymentDiagnosticEvent diagnosticEvent);
}

/// <summary>Result of diagnostic redaction.</summary>
public sealed record RedactionResult(DeploymentDiagnosticEvent Event, int RedactedValueCount);
