namespace Infrastructure.Observability.OpenTelemetry;

/// <summary>
///     Controls OpenTelemetry export without placing credentials in application settings.
/// </summary>
public sealed class OpenTelemetryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OpenTelemetry";

    /// <summary>Logical service name exported with telemetry.</summary>
    public string ServiceName { get; set; } = "AutoMate";

    /// <summary>Deployment environment label. When omitted, the host environment name is used.</summary>
    public string? Environment { get; init; }

    /// <summary>Enables console export, intended for development and local diagnostics.</summary>
    public bool ExportConsole { get; init; }

    /// <summary>
    ///     Optional absolute HTTP(S) collector URL without URL credentials, query or fragment;
    ///     authentication belongs in protected exporter configuration.
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>Retained fraction of fast successful SignalR sends.</summary>
    public double SignalRSuccessSampleRate { get; init; } = 0.01;

    /// <summary>Retained fraction of fast empty analysis claims.</summary>
    public double EmptyClaimSampleRate { get; init; } = 0.01;

    /// <summary>SignalR sends at or above this duration are always retained.</summary>
    public int SignalRSlowMilliseconds { get; init; } = 100;

    /// <summary>Analysis claims at or above this duration are always retained.</summary>
    public int ClaimSlowMilliseconds { get; init; } = 250;
}
