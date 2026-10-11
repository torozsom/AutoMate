namespace Application.Abstractions.Diagnostics;

/// <summary>Private host protocol information; contains no tenant data, endpoints or credentials.</summary>
public sealed record TelemetryCapabilities(int Version, IReadOnlyList<string> ArchiveOperations)
{
    /// <summary>Protocol version understood by this Web/Telemetry revision.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Archive operations required before Web reads or maintains permanent history.</summary>
    public static IReadOnlyList<string> RequiredArchiveOperations { get; } = Array.AsReadOnly(new[]
    {
        "logs", "metrics", "metric-batch", "assessment", "assessment-metrics", "import", "import-metrics", "delete"
    });
}