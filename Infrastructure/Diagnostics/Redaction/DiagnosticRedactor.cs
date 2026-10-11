using System.Text.RegularExpressions;
using Application.Abstractions.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>Conservatively removes common credentials from normalized diagnostics.</summary>
public sealed partial class DiagnosticRedactor : IDiagnosticRedactor
{
    /// <summary>Stable replacement for sensitive or unprocessable input.</summary>
    private const string RedactedMarker = "[REDACTED]";

    /// <summary>Maximum input scanned per field, including standalone analysis context.</summary>
    private const int MaximumInputCharacters = 131_072;

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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in diagnosticEvent.Attributes.Take(32))
            {
                var safeKey = SafeField(key, 128);
                var redactedValue = IsSensitiveKey(key) ? RedactedMarker : RedactText(value, 1024);
                if (safeKey != key || redactedValue != value) redactedCount++;
                attributes[safeKey] = redactedValue;
            }
        }

        var traceId = ValidTraceField(diagnosticEvent.TraceId, 32);
        var spanId = ValidTraceField(diagnosticEvent.SpanId, 16);
        if (traceId != diagnosticEvent.TraceId) redactedCount++;
        if (spanId != diagnosticEvent.SpanId) redactedCount++;

        return new RedactionResult(diagnosticEvent with
        {
            Message = redactedMessage,
            Attributes = attributes ?? diagnosticEvent.Attributes,
            SourceIdentity = diagnosticEvent.SourceIdentity is { } identity
                ? identity with
                {
                    InstanceId = CountedField(identity.InstanceId, 128, ref redactedCount)
                }
                : null,
            Cursor = CountedField(diagnosticEvent.Cursor, 512, ref redactedCount),
            TraceId = traceId,
            SpanId = spanId,
            Metrics = diagnosticEvent.Metrics?.Take(32).Select(sample => sample with
            {
                Name = CountedField(sample.Name, 128, ref redactedCount)!,
                Unit = CountedField(sample.Unit, 64, ref redactedCount)!
            }).ToArray(),
            TerminalChannel = diagnosticEvent.TerminalChannel with
            {
                Target = CountedField(diagnosticEvent.TerminalChannel.Target, 128, ref redactedCount)
            }
        }, redactedCount);
    }

    /// <inheritdoc />
    public string RedactText(string value, int maximumCharacters = 4096)
    {
        ArgumentNullException.ThrowIfNull(value);
        var maximum = Math.Clamp(maximumCharacters, 1, MaximumInputCharacters);
        if (value.Length > MaximumInputCharacters) return RedactedMarker[..Math.Min(maximum, RedactedMarker.Length)];
        string safe;
        try
        {
            // Normalize controls first so escape sequences cannot split credential names or values.
            safe = MaskText(SanitizeControls(value));
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactedMarker[..Math.Min(maximum, RedactedMarker.Length)];
        }

        const string suffix = " [output truncated]\r\n";
        if (safe.Length <= maximum) return safe;
        var marker = suffix[..Math.Min(maximum, suffix.Length)];
        return safe[..(maximum - marker.Length)] + marker;
    }

    /// <summary>Bounds and redacts provider-controlled correlation fields.</summary>
    private string SafeField(string value, int maximum)
    {
        return RedactText(value, maximum);
    }

    /// <summary>Counts changed optional source/metric fields without retaining their original values.</summary>
    private string? CountedField(string? value, int maximum, ref int count)
    {
        if (value is null) return null;
        var safe = SafeField(value, maximum);
        if (safe != value) count++;
        return safe;
    }

    /// <summary>Accepts only canonical nonzero hexadecimal trace identifiers.</summary>
    private static string? ValidTraceField(string? value, int length)
    {
        return value is not null && value.Length == length && value.Any(character => character != '0') &&
               value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : null;
    }

    /// <summary>Recognizes sensitive attribute/environment names across common separators.</summary>
    private static bool IsSensitiveKey(string key)
    {
        var normalized = string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("cookie", StringComparison.Ordinal) ||
               normalized.Contains("connectionstring", StringComparison.Ordinal) ||
               normalized.Contains("privatekey", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("accesskey", StringComparison.Ordinal) ||
               normalized.Contains("accountkey", StringComparison.Ordinal) ||
               normalized.Contains("passphrase", StringComparison.Ordinal) ||
               normalized.Contains("pwd", StringComparison.Ordinal) || normalized == "sig" ||
               normalized.EndsWith("pass", StringComparison.Ordinal);
    }

    /// <summary>Masks supported credential forms without changing surrounding diagnostic prose.</summary>
    private static string MaskText(string value)
    {
        var safe = PrivateKeyPattern().Replace(value, RedactedMarker);
        safe = CookiePattern().Replace(safe, "${prefix}" + RedactedMarker);
        safe = AuthorizationPattern().Replace(safe, "${prefix}" + RedactedMarker);
        safe = NamedSecretPattern().Replace(safe, match =>
        {
            var content = match.Groups["value"].Value;
            var quote = content.StartsWith('"') ? "\"" : content.StartsWith('\'') ? "'" : "";
            return match.Groups["prefix"].Value + quote + RedactedMarker + quote;
        });
        safe = UrlCredentialsPattern().Replace(safe, "${scheme}" + RedactedMarker + "@");
        safe = JwtPattern().Replace(safe, RedactedMarker);
        return ProviderTokenPattern().Replace(safe, RedactedMarker);
    }

    /// <summary>Removes unsafe controls while retaining legitimate terminal line/progress characters.</summary>
    private static string SanitizeControls(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var withoutSequences = TerminalEscapePattern().Replace(value, string.Empty);
        return UnsafeControlPattern().Replace(withoutSequences, string.Empty);
    }

    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07\\x1B]*(?:\\x07|\\x1B\\\\)|[@-_])",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex TerminalEscapePattern();

    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.CultureInvariant, 100)]
    private static partial Regex UnsafeControlPattern();

    /// <summary>Quoted JSON, connection-string and environment assignments with sensitive names.</summary>
    [GeneratedRegex(
        """\b(?<prefix>["']?[A-Za-z0-9_.:-]*?(?:password|pwd|secret|token|api[_-]?key|private[_-]?key|access[_-]?key|account[_-]?key|passphrase|(?:db|database|smtp|redis|mysql|postgres)[_-]?pass|\bsig|credential|cookie|authorization)[A-Za-z0-9_.:-]*["']?\s*[=:]\s*)(?<value>"(?:\\.|[^"\\])*(?:"|$)|'[^']*(?:'|$)|[^\s,;&"']+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex NamedSecretPattern();

    /// <summary>Authorization schemes, including standalone bearer/basic text.</summary>
    [GeneratedRegex("""(?<prefix>\b(?:bearer|basic)\s+)[^\s,;&"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthorizationPattern();

    /// <summary>Cookie headers are masked as a whole rather than guessing secret cookie names.</summary>
    [GeneratedRegex("""(?<prefix>\b(?:set-cookie|cookie)\s*[:=]\s*)[^\r\n]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CookiePattern();

    /// <summary>PEM private keys, including unterminated keys and escaped JSON newlines.</summary>
    [GeneratedRegex(
        """-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z0-9]+ )*PRIVATE KEY-----|$)""",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex PrivateKeyPattern();

    /// <summary>URI user information is sensitive even without a named password field.</summary>
    [GeneratedRegex("""(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/@]+:[^\s/@]+@""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlCredentialsPattern();

    /// <summary>Recognizes JWT-shaped credential values.</summary>
    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex JwtPattern();

    /// <summary>Recognizes GitHub classic/fine-grained and OpenAI-style credentials.</summary>
    [GeneratedRegex(
        """\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,})\b""",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex ProviderTokenPattern();
}