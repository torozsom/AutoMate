using System.Collections;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Application.Abstractions.Diagnostics;
using Application.Diagnostics;
using Domain.Enums;

namespace Infrastructure.Observability;

/// <summary>Explicit platform telemetry allowlists; terminal diagnostics use their separate redacted storage pipeline.</summary>
public sealed partial class PlatformTelemetryPolicy(IDiagnosticRedactor redactor)
{
    /// <summary>Safe fallback for unknown templates and free-form messages.</summary>
    public const string UnknownMessage = "Platform event; unapproved details omitted.";

    /// <summary>GUID fields permitted for correlation; owner/user identifiers are excluded.</summary>
    private static readonly HashSet<string> GuidFields = new(StringComparer.Ordinal)
    {
        "ProjectId", "DeploymentId", "AnalysisId", "RunId", "Id", "AppId", "UserId", "RequestId",
        "deployment.project.id", "deployment.id", "deployment.analysis.id"
    };

    /// <summary>Numeric operational fields, never arbitrary external attributes.</summary>
    private static readonly HashSet<string> NumericFields = new(StringComparer.Ordinal)
    {
        "Count", "FileCount", "Attempt", "AttemptCount", "RedactedValueCount", "WaitMs", "DelaySeconds",
        "HostPort", "ContainerPort", "Port", "JobId", "Code", "StatusCode", "http.response.status_code",
        "http.status_code", "network.protocol.version", "ElapsedMilliseconds", "DurationMs", "EventCode"
    };

    /// <summary>Known failure classes; unknown exception/type names cannot become exported payloads.</summary>
    private static readonly HashSet<string> FailureTypes = new(StringComparer.Ordinal)
    {
        "Exception", "IOException", "InvalidOperationException", "ArgumentException", "ArgumentNullException",
        "OperationCanceledException", "TaskCanceledException", "TimeoutException", "HttpRequestException",
        "UnauthorizedAccessException", "JsonException", "DbUpdateException", "DbUpdateConcurrencyException",
        "NpgsqlException", "PostgresException", "RedisConnectionException", "RedisTimeoutException",
        "DockerApiException", "DockerContainerNotFoundException", "SocketException", "CryptographicException",
        "InvalidAnalysisResultException",
        "AnalysisProviderUnavailableException", "TelemetryProviderException", "ObjectDisposedException"
    };

    /// <summary>Allows source categories from reviewed code only, retaining existing filters for known categories.</summary>
    public string Category(string? category)
    {
        return category is not null && PlatformLogCatalog.Categories.Contains(category)
            ? category
            : "AutoMate.Platform";
    }

    /// <summary>Snapshots approved typed fields without invoking arbitrary objects' formatters or ToString methods.</summary>
    public Dictionary<string, object?> Fields(object? state)
    {
        var safe = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return safe;
        try
        {
            foreach (var pair in fields.Take(64))
            {
                var value = Field(pair.Key, pair.Value);
                if (value is not null) safe[pair.Key] = value;
            }
        }
        catch (Exception)
        {
            // An untrusted enumerable must not fail deployment processing or partially expose its values.
            safe.Clear();
        }

        return safe;
    }

    /// <summary>Validates field names and their value domains together.</summary>
    public object? Field(string key, object? value)
    {
        if (key == "UserId" && value is null or "unavailable") return "unavailable";
        if (GuidFields.Contains(key))
            return value is Guid id && id != Guid.Empty ? id : null;
        if (NumericFields.Contains(key))
        {
            if (value is HttpStatusCode status) return (int)status;
            return value switch
            {
                byte or short or int or long or uint or ulong or decimal => value,
                float number when float.IsFinite(number) => number,
                double number when double.IsFinite(number) => number,
                _ => null
            };
        }

        if (key == "Operation") return EnumValue<AuditOperation>(value);
        if (key == "Outcome") return EnumValue<AuditOutcome>(value);
        if (key == "ComposeOutcome")
            return value is "completed" or "failed" or "cancelled" or "timed-out" or "unavailable" ? value : null;
        if (key == "DbType")
            return value is "PostgreSQL" or "MySQL" or "SQLServer" or "MongoDB" or "Redis" or "Oracle" ? value : null;
        if (key == "RequestArea")
            return value is "Authentication" or "Deployments" or "SignalR" or "Health" or "Application" ? value : null;
        if (key == "AuthenticationScheme")
            return value is "Cookies" or "GitHub" or "Microsoft" ? value : null;
        if (key == "ConsentState") return value is "enabled" or "disabled" ? value : null;
        if (key == "OutputFile")
            return value is "Dockerfile" or "Dockerfile.dockerignore" or "docker-compose.yml" or "main.bicep" or "deploy.yml" or "infra/main.bicep" or
                ".automate/Dockerfile" or ".automate/Dockerfile.dockerignore" or ".github/workflows/deploy.yml"
                ? value : null;
        if (key is "Source" or "DiagnosticSource" or "deployment.source")
            return value is "SignalR" ? "SignalR" : EnumValue<DeploymentDiagnosticSource>(value);
        if (key is "Kind" or "DiagnosticKind" or "deployment.kind") return EnumValue<DeploymentDiagnosticKind>(value);
        if (key is "Status") return EnumValue<DeploymentStatus>(value);
        if (key == "deployment.severity") return EnumValue<DeploymentDiagnosticSeverity>(value);
        if (key == "deployment.channel") return EnumValue<DeploymentTerminalChannelKind>(value);
        if (key == "FailureType")
            return value is string failure && FailureTypes.Contains(failure) ? failure : "Exception";
        if (key is "AuthenticationState" or "security.authentication_state")
            return value is "authenticated" or "anonymous" ? value : null;
        if (key == "JobType")
            return value is "LocalDeploymentJob" or "CloudDeploymentJob" or "StopLocalDeploymentJob" ? value : null;
        if (key == "deployment.outcome")
            return value is "completed" or "failed" or "canceled" or "denied" or "timed_out" ? value : null;
        if (key == "analysis.outcome")
            return value is "completed" or "failed" or "canceled" or "skipped" or "empty" or "discarded"
                or "retry_scheduled"
                ? value
                : null;
        if (key is "http.request.method" or "http.method" or "HttpMethod" or "RequestMethod")
        {
            if (value is System.Net.Http.HttpMethod method) value = method.Method;
            return value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" or "CONNECT"
                or "TRACE"
                ? value
                : null;
        }
        if (key == "url.scheme") return value is "http" or "https" ? value : null;
        if (key is "db.system" or "db.system.name") return value is "postgresql" or "sqlite" or "redis" ? value : null;
        return null;
    }

    /// <summary>Builds detached structured state from approved literals and typed properties.</summary>
    public SafePlatformLog Log(object? state, string? category = null, int eventCode = 0, Exception? exception = null)
    {
        var fields = Fields(state);
        string? template = null;
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            try
            {
                template = values.Take(64).FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;
            }
            catch (Exception)
            {
                template = null;
            }

        template = template switch
        {
            "Application started. Press Ctrl+C to shut down." => "Application started.",
            "Application is shutting down..." => "Application is shutting down.",
            "Hosting environment: {EnvName}" => "Hosting environment initialized.",
            "Content root path: {ContentRoot}" => "Content root initialized.",
            "Now listening on: {address}" => "HTTP listener started.",
            "Start processing HTTP request {HttpMethod} {Uri}" => "Outbound HTTP request started: method {HttpMethod}.",
            "Sending HTTP request {HttpMethod} {Uri}" => "Outbound HTTP request sent: method {HttpMethod}.",
            "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}" =>
                "Outbound HTTP response received: status {StatusCode}, duration {ElapsedMilliseconds} ms.",
            "End processing HTTP request after {ElapsedMilliseconds}ms - {StatusCode}" =>
                "Outbound HTTP request completed: status {StatusCode}, duration {ElapsedMilliseconds} ms.",
            "AuthenticationScheme: {AuthenticationScheme} signed in." => "Authentication scheme {AuthenticationScheme} signed in.",
            "AuthenticationScheme: {AuthenticationScheme} signed out." => "Authentication scheme {AuthenticationScheme} signed out.",
            "AuthenticationScheme: {AuthenticationScheme} was challenged." => "Authentication scheme {AuthenticationScheme} challenged.",
            "AuthenticationScheme: {AuthenticationScheme} was forbidden." => "Authentication scheme {AuthenticationScheme} forbidden.",
            "AuthenticationScheme: {AuthenticationScheme} was successfully authenticated." =>
                "Authentication scheme {AuthenticationScheme} authenticated.",
            "No migrations were applied. The database is already up to date." => "Database migrations are current.",
            _ => template is not null && PlatformLogCatalog.Templates.Contains(template) ? template : UnknownMessage
        };
        if (exception is not null) fields["FailureType"] = Field("FailureType", exception.GetType().Name);
        if (template == UnknownMessage && category is not null && category != "AutoMate.Platform" && PlatformLogCatalog.Categories.Contains(category))
        {
            fields["EventCode"] = eventCode;
            template = exception is null
                ? "Module event {EventCode}; additional text fields withheld."
                : "Module event {EventCode} failed: {FailureType}; additional text fields withheld.";
        }
        var message = Placeholder().Replace(template, match => fields.TryGetValue(match.Groups[1].Value, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "[REDACTED]"
            : "[REDACTED]");
        fields["{OriginalFormat}"] = template;
        return new SafePlatformLog(redactor.RedactText(message), fields.ToArray());
    }

    /// <summary>Returns only explicitly named enum values, including typed enums without arbitrary formatting.</summary>
    private static string? EnumValue<T>(object? value) where T : struct, Enum
    {
        if (value is T item && Enum.IsDefined(item)) return Enum.GetName(item);
        return value is string name && Enum.GetNames<T>().Contains(name, StringComparer.Ordinal) ? name : null;
    }

    /// <summary>Finds structured placeholders while ignoring alignment/format specifiers.</summary>
    [GeneratedRegex(@"\{([A-Za-z0-9_.]+)(?:[^{}]*)\}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Placeholder();
}

/// <summary>Immutable safe ILogger state, also exported as structured SDK attributes.</summary>
public sealed class SafePlatformLog(string message, IReadOnlyList<KeyValuePair<string, object?>> fields)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    /// <summary>Safe formatted message.</summary>
    public string Message { get; } = message;

    /// <inheritdoc />
    public int Count => fields.Count;

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index] => fields[index];

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        return fields.GetEnumerator();
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Message;
    }
}
