namespace Application.Diagnostics;

/// <summary>Controls the in-memory diagnostic delivery boundary.</summary>
public sealed class DeploymentDiagnosticOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DeploymentDiagnostics";

    /// <summary>Maximum number of already-redacted events awaiting terminal delivery.</summary>
    public int BufferCapacity { get; init; } = 512;

    /// <summary>Maximum time for durable storage admission, including capacity waits.</summary>
    public int PersistenceTimeoutSeconds { get; init; } = 10;

    /// <summary>Maximum time for a live transport write; saved output remains available for replay.</summary>
    public int DeliveryTimeoutSeconds { get; init; } = 2;
}