namespace Application.Diagnostics;

/// <summary>Controls the in-memory diagnostic delivery boundary.</summary>
public sealed class DeploymentDiagnosticOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DeploymentDiagnostics";

    /// <summary>Maximum number of already-redacted events awaiting terminal delivery.</summary>
    public int BufferCapacity { get; init; } = 512;
}