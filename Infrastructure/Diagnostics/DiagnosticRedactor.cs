using System.Text.RegularExpressions;
using Application.Abstractions.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>Conservatively removes common credentials from normalized diagnostics.</summary>
public sealed partial class DiagnosticRedactor : IDiagnosticRedactor
{
    private const string RedactedMarker = "[REDACTED]";

    /// <inheritdoc />
    public RedactionResult Redact(DeploymentDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        var redactedMessage = SanitizeControls(RedactText(diagnosticEvent.Message));
        var redactedCount = redactedMessage == diagnosticEvent.Message ? 0 : 1;
        Dictionary<string, string>? attributes = null;

        if (diagnosticEvent.Attributes is not null)
        {
            attributes =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in diagnosticEvent.Attributes.Take(32))
            {
                var redactedValue = IsSensitiveKey(key) ? RedactedMarker : SanitizeControls(RedactText(value));
                if (!string.Equals(redactedValue, value, StringComparison.Ordinal)) redactedCount++;
                attributes[SafeField(key, 128)] = redactedValue[..Math.Min(1024, redactedValue.Length)];
            }
        }

        return new RedactionResult(diagnosticEvent with
        {
            Message = redactedMessage.Length <= 4096
                ? redactedMessage
                : redactedMessage[..4096] + " [output truncated]\r\n",
            Attributes = attributes ?? diagnosticEvent.Attributes,
            SourceIdentity = diagnosticEvent.SourceIdentity is { } identity
                ? identity with
                {
                    InstanceId = identity.InstanceId is null ? null : SafeField(identity.InstanceId, 128)
                }
                : null,
            Cursor = diagnosticEvent.Cursor is null ? null : SafeField(diagnosticEvent.Cursor, 512),
            TraceId = diagnosticEvent.TraceId is null ? null : SafeField(diagnosticEvent.TraceId, 32),
            SpanId = diagnosticEvent.SpanId is null ? null : SafeField(diagnosticEvent.SpanId, 16),
            TerminalChannel = diagnosticEvent.TerminalChannel with
            {
                Target = diagnosticEvent.TerminalChannel.Target is null
                    ? null
                    : SafeField(diagnosticEvent.TerminalChannel.Target, 128)
            }
        }, redactedCount);
    }

    /// <summary>Bounds and redacts provider-controlled correlation fields.</summary>
    private static string SafeField(string value, int maximum)
    {
        var safe = SanitizeControls(RedactText(value));
        return safe[..Math.Min(maximum, safe.Length)];
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("password", StringComparison.OrdinalIgnoreCase)
               || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
               || key.Contains("token", StringComparison.OrdinalIgnoreCase)
               || key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
               || key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
               || key.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
               || key.Equals("pwd", StringComparison.OrdinalIgnoreCase)
               || key.Equals("apikey", StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var redacted = NamedSecretPattern().Replace(value, "${name}=" + RedactedMarker);
        redacted = AuthorizationPattern().Replace(redacted, "$1 " + RedactedMarker);
        redacted = JwtPattern().Replace(redacted, RedactedMarker);
        return GitHubTokenPattern().Replace(redacted, RedactedMarker);
    }

    private static string SanitizeControls(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var withoutSequences = TerminalEscapePattern().Replace(value, string.Empty);
        return UnsafeControlPattern().Replace(withoutSequences, string.Empty);
    }

    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07\\x1B]*(?:\\x07|\\x1B\\\\)|[@-_])",
        RegexOptions.CultureInvariant)]
    private static partial Regex TerminalEscapePattern();

    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeControlPattern();

    [GeneratedRegex(
        "(?<name>password|pwd|client_secret|access_token|refresh_token|api[_-]?key|token)\\s*[=:]\\s*(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretPattern();

    [GeneratedRegex("(authorization\\s*[:=]\\s*(?:bearer|basic))\\s+[^\\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex("\\bgh[pousr]_[A-Za-z0-9]{20,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPattern();
}