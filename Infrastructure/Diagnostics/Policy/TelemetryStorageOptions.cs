namespace Infrastructure.Diagnostics;

/// <summary>Specialized storage configuration; secrets must come from environment or a secret store.</summary>
public sealed class TelemetryStorageOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "TelemetryStorage";

    /// <summary>Postgres preserves existing installations; LokiMimir enables external delivery.</summary>
    public string Backend { get; set; } = "Postgres";

    /// <summary>DiskGateway bypasses the application database for new telemetry payloads.</summary>
    public string DeliveryMode { get; set; } = "PostgresOutbox";

    public string GatewayUrl { get; set; } = "";
    public string GatewayToken { get; set; } = "";
    public bool DiskGateway => DeliveryMode == "DiskGateway";

    /// <summary>Loki base URI including any gateway prefix.</summary>
    public string LokiUrl { get; set; } = "";

    /// <summary>Mimir OTLP metrics endpoint, also supported by managed OTLP gateways.</summary>
    public string MetricsWriteUrl { get; set; } = "";

    /// <summary>Prometheus query API base URI including /prometheus/ for Mimir.</summary>
    public string MetricsQueryUrl { get; set; } = "";

    /// <summary>Gateway authorization header supplied through a secret store.</summary>
    public string Authorization { get; set; } = "";

    /// <summary>Optional operator-selected public CA certificate for a private TLS gateway; applies only to telemetry HTTP.</summary>
    public string CaCertificatePath { get; set; } = "";

    /// <summary>Allows plaintext transport only on private development networks.</summary>
    public bool AllowInsecureDevelopment { get; set; }

    /// <summary>External managed storage requires approved region, DPA, and explicit consent.</summary>
    public bool ManagedService { get; set; }

    /// <summary>Operator attestation that regional and contractual review is complete.</summary>
    public bool ManagedDataProcessingApproved { get; set; }

    /// <summary>Approved processing region disclosed before managed-provider consent.</summary>
    public string ProcessingRegion { get; set; } = "";

    /// <summary>Maximum redacted buffer age during outages.</summary>
    public int BufferHours { get; set; } = 24;

    /// <summary>Per-owner buffer byte budget.</summary>
    public long TenantBufferBytes { get; set; } = 128 * 1024 * 1024;

    /// <summary>Cluster-wide buffer byte budget.</summary>
    public long GlobalBufferBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum tenant diagnostic ingestion bytes in a rolling minute.</summary>
    public long TenantBytesPerMinute { get; set; } = 8 * 1024 * 1024;

    /// <summary>Maximum simultaneous container identities per owner.</summary>
    public int MaximumMetricContainers { get; set; } = 100;

    /// <summary>Bounded delivery batch size.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Process-local expensive query concurrency bound.</summary>
    public int QueryConcurrency { get; set; } = 4;

    /// <summary>Runtime metric interval in seconds.</summary>
    public int RuntimeSampleSeconds { get; set; } = 60;

    /// <summary>Whether specialized storage is selected.</summary>
    public bool Specialized => Backend == "LokiMimir";

    /// <summary>Validates security and finite resource limits before startup.</summary>
    public bool IsValid()
    {
        return !GetValidationErrors().Any();
    }

    /// <summary>Reports actionable setting names without exposing configured URLs, tokens, or other values.</summary>
    public IEnumerable<string> GetValidationErrors()
    {
        if (Backend != "Postgres" && !Specialized)
            yield return "TelemetryStorage:Backend must be Postgres or LokiMimir.";
        if (DeliveryMode is not ("PostgresOutbox" or "DiskGateway"))
            yield return "TelemetryStorage:DeliveryMode must be PostgresOutbox or DiskGateway.";
        if (DiskGateway && (!Specialized || !ValidUri(GatewayUrl) || GatewayToken.Length < 32))
            yield return
                "DiskGateway requires LokiMimir, an approved GatewayUrl and a GatewayToken of at least 32 characters.";
        if (BufferHours is < 1 or > 24) yield return "TelemetryStorage:BufferHours must be between 1 and 24.";
        if (BatchSize is < 1 or > 500) yield return "TelemetryStorage:BatchSize must be between 1 and 500.";
        if (TenantBufferBytes <= 0) yield return "TelemetryStorage:TenantBufferBytes must be positive.";
        if (GlobalBufferBytes < TenantBufferBytes)
            yield return "TelemetryStorage:GlobalBufferBytes must cover TenantBufferBytes.";
        if (TenantBytesPerMinute <= 0) yield return "TelemetryStorage:TenantBytesPerMinute must be positive.";
        if (MaximumMetricContainers is < 1 or > 1000)
            yield return "TelemetryStorage:MaximumMetricContainers must be between 1 and 1000.";
        if (QueryConcurrency is < 1 or > 32) yield return "TelemetryStorage:QueryConcurrency must be between 1 and 32.";
        if (RuntimeSampleSeconds < 60) yield return "TelemetryStorage:RuntimeSampleSeconds must be at least 60.";
        if (!Specialized) yield break;
        if (!string.IsNullOrWhiteSpace(CaCertificatePath) && !File.Exists(CaCertificatePath))
            yield return "TelemetryStorage:CaCertificatePath must reference an existing public CA certificate file.";
        foreach (var endpoint in new[]
                 {
                     (nameof(LokiUrl), LokiUrl), (nameof(MetricsWriteUrl), MetricsWriteUrl),
                     (nameof(MetricsQueryUrl), MetricsQueryUrl)
                 })
            if (!ValidUri(endpoint.Item2))
                yield return
                    $"TelemetryStorage:{endpoint.Item1} requires an absolute HTTPS URL without embedded credentials. Load the complete pilot configuration, not just Backend.";
        if (ManagedService && !ManagedDataProcessingApproved)
            yield return
                "Managed telemetry requires TelemetryStorage:ManagedDataProcessingApproved after provider review.";
        if (ManagedService && string.IsNullOrWhiteSpace(ProcessingRegion))
            yield return "Managed telemetry requires TelemetryStorage:ProcessingRegion.";
    }

    /// <summary>Rejects userinfo and unapproved plaintext endpoints.</summary>
    private bool ValidUri(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme == "https" ||
                                                      (AllowInsecureDevelopment && !ManagedService &&
                                                       uri.Scheme == "http"));
    }
}