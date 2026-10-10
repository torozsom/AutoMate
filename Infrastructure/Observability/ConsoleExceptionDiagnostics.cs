using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Observability;

/// <summary>Writes bounded, redacted exception snapshots only to the operator console, never to ILogger providers.</summary>
public sealed class ConsoleExceptionDiagnostics(
    IDiagnosticRedactor redactor,
    IConfiguration? configuration = null,
    TextWriter? writer = null,
    IOptionsMonitor<LoggerFilterOptions>? filters = null)
{
    /// <summary>Maximum UTF-8 bytes in one diagnostic record, including its final newline.</summary>
    public const int MaximumBytes = 16 * 1024;

    /// <summary>Serializes complete records from concurrent worker slots without owning the console writer.</summary>
    private readonly object gate = new();

    /// <summary>Snapshots approved correlation and exception details; any formatting or console failure is isolated.</summary>
    public void Write(string category, LogLevel level, int eventId, SafePlatformLog state,
        IExternalScopeProvider scopes, Exception exception)
    {
        try
        {
            if (!ConsoleEnabled(category, level)) return;
            var credentials = configuration?.AsEnumerable()
                .Where(pair => SensitiveKey(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                .Select(pair => pair.Value!).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length)
                .ToArray() ?? [];
            var text = new StringBuilder();
            text.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(' ').Append(level).Append(' ').Append(category).Append('[').Append(eventId)
                .AppendLine("] exception diagnostics:");
            var correlation = new Dictionary<string, Guid>(StringComparer.Ordinal);
            scopes.ForEachScope((scope, fields) =>
            {
                if (scope is SafePlatformLog safe)
                    foreach (var field in safe)
                        if (field.Value is Guid id)
                            fields[field.Key] = id;
            }, correlation);
            foreach (var field in state)
                if (field.Value is Guid id)
                    correlation[field.Key] = id;
            foreach (var field in correlation) text.Append(field.Key).Append('=').Append(field.Value).Append(' ');
            if (Activity.Current is { } activity)
                text.Append("TraceId=").Append(activity.TraceId).Append(" SpanId=").Append(activity.SpanId);
            text.AppendLine();
            var pending = new Queue<(Exception Error, int Depth)>();
            pending.Enqueue((exception, 0));
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            var count = 0;
            while (pending.TryDequeue(out var entry) && count < 16 && text.Length < MaximumBytes)
            {
                if (!seen.Add(entry.Error)) continue;
                count++;
                text.Append("Exception depth ").Append(entry.Depth).Append(": ")
                    .Append(Mask(entry.Error.GetType().FullName ?? "Exception", credentials, 512)).Append(": ")
                    .AppendLine(Mask(ExceptionMessage(entry.Error), credentials, 4096));
                if (entry.Error is HttpRequestException { StatusCode: { } status })
                    text.Append("HTTP status: ").Append((int)status).AppendLine();
                foreach (var frame in new StackTrace(entry.Error, false).GetFrames()?.Take(20) ?? [])
                {
                    var method = frame.GetMethod();
                    // Reflection supplies symbols only; no source files, argument values or raw StackTrace text.
                    text.Append("  at ").Append(Mask(
                        (method?.DeclaringType?.FullName ?? "unknown") + "." + (method?.Name ?? "unknown"),
                        credentials, 512)).AppendLine();
                }

                var children = entry.Error is AggregateException aggregate
                    ? aggregate.InnerExceptions.Take(16)
                    : entry.Error.InnerException is { } inner
                        ? [inner]
                        : Enumerable.Empty<Exception>();
                if (entry.Depth < 3)
                    foreach (var child in children)
                        pending.Enqueue((child, entry.Depth + 1));
                else if (children.Any()) text.AppendLine("[inner exceptions truncated]");
            }

            if (pending.Count > 0) text.AppendLine("[exceptions truncated]");
            var safeText = Bound(Mask(text.ToString(), credentials, MaximumBytes));
            lock (gate)
            {
                (writer ?? Console.Error).WriteLine(safeText);
            }
        }
        catch (Exception)
        {
            // Exception getters, configuration providers and console writers must never break business processing.
        }
    }

    /// <summary>Applies standard console provider/category filter precedence independently of other enabled providers.</summary>
    private bool ConsoleEnabled(string category, LogLevel level)
    {
        if (filters is null) return true;
        const string provider = "Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider";
        var settings = filters.CurrentValue;
        LoggerFilterRule? selected = null;
        foreach (var rule in settings.Rules)
        {
            if (rule.ProviderName is not null && rule.ProviderName != provider && rule.ProviderName != "Console")
                continue;
            if (rule.CategoryName is not null &&
                !category.StartsWith(rule.CategoryName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (selected is not null)
            {
                if (selected.ProviderName is not null && rule.ProviderName is null) continue;
                if (selected.ProviderName is null == rule.ProviderName is null &&
                    (selected.CategoryName?.Length ?? 0) > (rule.CategoryName?.Length ?? 0)) continue;
            }

            selected = rule;
        }

        var minimum = selected is null ? settings.MinLevel : selected.LogLevel;
        return (minimum is null || level >= minimum) && (selected?.Filter?.Invoke(provider, category, level) ?? true);
    }

    /// <summary>Provider and database SDK messages may embed whole response/SQL payloads, so retain their type only.</summary>
    private static string ExceptionMessage(Exception error)
    {
        var type = error.GetType().FullName ?? "";
        return type.StartsWith("Azure.", StringComparison.Ordinal) ||
               type.StartsWith("Docker.DotNet.", StringComparison.Ordinal) ||
               type.StartsWith("Octokit.", StringComparison.Ordinal) ||
               type.StartsWith("Npgsql.", StringComparison.Ordinal) ||
               type.StartsWith("Microsoft.Data.", StringComparison.Ordinal)
            ? "[provider details omitted]"
            : error.Message;
    }

    /// <summary>Masks configured credentials before central normalization, URL/body suppression and truncation.</summary>
    private string Mask(string text, string[] credentials, int maximum)
    {
        if (text.Length > 131072) return "[REDACTED]";
        foreach (var credential in credentials)
            text = text.Replace(credential, "[REDACTED]", StringComparison.Ordinal);
        return ConsoleDiagnosticText.Redact(redactor.RedactText(text, 131072), maximum);
    }

    /// <summary>Recognizes credential configuration sections and common protected option names.</summary>
    private static bool SensitiveKey(string key)
    {
        var normalized = string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return new[]
            {
                "password", "pwd", "secret", "token", "apikey", "accesskey", "accountkey",
                "privatekey", "credential", "connectionstring", "authorization", "cookie", "passphrase"
            }
            .Any(normalized.Contains);
    }

    /// <summary>Bounds UTF-8 bytes without splitting a surrogate pair; leaves space for the console newline.</summary>
    private static string Bound(string value)
    {
        var maximum = MaximumBytes - Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (Encoding.UTF8.GetByteCount(value) <= maximum) return value;
        const string marker = "\n[exception diagnostics truncated]";
        var available = maximum - Encoding.UTF8.GetByteCount(marker);
        var bytes = 0;
        var characters = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > available) break;
            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }

        return value[..characters] + marker;
    }
}

/// <summary>Additional console-only masking for provider URLs, body/header dumps and source paths in messages.</summary>
internal static partial class ConsoleDiagnosticText
{
    /// <summary>Suppresses common payload fields and paths before applying the output character bound.</summary>
    internal static string Redact(string text, int maximum)
    {
        try
        {
            text = Payload().Replace(text, "${prefix}[REDACTED]");
            text = Address().Replace(text, "[REDACTED]");
            return text.Length <= maximum ? text : text[..maximum] + " [truncated]";
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }
    }

    /// <summary>Provider body and header dumps are excluded rather than credential-masked as arbitrary prose.</summary>
    [GeneratedRegex(
        @"(?is)(?<prefix>\b(?:response\s*body|request\s*body|headers|body|content)\s*[:=]).*",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex Payload();

    /// <summary>Addresses and absolute Windows/Unix paths are not useful console exception context.</summary>
    [GeneratedRegex(
        @"(?i)\bhttps?://[^\s""'<>]+|\b[A-Z]:[\\/][^\r\n""']*|(?<![\w:])/(?:[^\s/]+/)*[^\s]+",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex Address();
}