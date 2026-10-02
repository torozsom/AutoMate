namespace Web.Configs;

/// <summary>Controls OpenTelemetry export without placing credentials in application settings.</summary>
public sealed class OpenTelemetryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OpenTelemetry";

    /// <summary>Logical service name exported with telemetry.</summary>
    public string ServiceName { get; init; } = "AutoMate";

    /// <summary>Deployment environment label. When omitted, the host environment name is used.</summary>
    public string? Environment { get; init; }

    /// <summary>Enables console export, intended for development and local diagnostics.</summary>
    public bool ExportConsole { get; init; }

    /// <summary>Optional OTLP collector endpoint. Authentication is supplied outside tracked configuration.</summary>
    public string? OtlpEndpoint { get; init; }
}