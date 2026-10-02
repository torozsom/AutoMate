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

        var redactedMessage = RedactText(diagnosticEvent.Message);
        var redactedCount = redactedMessage == diagnosticEvent.Message ? 0 : 1;
        Dictionary<string, string>? attributes = null;

        if (diagnosticEvent.Attributes is not null)
        {
            attributes =
                new Dictionary<string, string>(diagnosticEvent.Attributes.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in diagnosticEvent.Attributes)
            {
                var redactedValue = IsSensitiveKey(key) ? RedactedMarker : RedactText(value);
                if (!string.Equals(redactedValue, value, StringComparison.Ordinal)) redactedCount++;
                attributes[key] = redactedValue;
            }
        }

        return new RedactionResult(diagnosticEvent with
        {
            Message = redactedMessage,
            Attributes = attributes ?? diagnosticEvent.Attributes
        }, redactedCount);
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