namespace Application.Abstractions.Diagnostics;

/// <summary>Redacts externally sourced diagnostic text and attributes before they reach any sink.</summary>
public interface IDiagnosticRedactor
{
    /// <summary>Returns a safe copy of the supplied event and the number of changed fields.</summary>
    RedactionResult Redact(DeploymentDiagnosticEvent diagnosticEvent);

    /// <summary>Redacts and bounds standalone transport/context text; oversized inputs fail closed.</summary>
    string RedactText(string value, int maximumCharacters = 4096);
}

/// <summary>Result of diagnostic redaction.</summary>
public sealed record RedactionResult(DeploymentDiagnosticEvent Event, int RedactedValueCount);