namespace Application.Abstractions.Diagnostics;

/// <summary>
///     Applies the shared text policy to every string in terminal transport/history
///     projections.
/// </summary>
public static class DiagnosticRedaction
{
    /// <summary>
    ///     Preserves routing identities and ordering while masking text and optional provider
    ///     metadata.
    /// </summary>
    /// <param name="redactor">The shared redaction boundary used before persistence or presentation.</param>
    /// <param name="log">The terminal projection to redact.</param>
    /// <returns>An independent terminal projection with unsafe text and metadata masked.</returns>
    /// <exception cref="ArgumentNullException">The redactor or terminal log is null.</exception>
    public static DeploymentTerminalLog RedactTerminal(this IDiagnosticRedactor redactor, DeploymentTerminalLog log)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(log);
        return log with
        {
            Message = redactor.RedactText(log.Message),
            TerminalChannel = redactor.RedactText(log.TerminalChannel, 128),
            SourceInstanceId = log.SourceInstanceId is null ? null : redactor.RedactText(log.SourceInstanceId, 128),
            SourceCursor = log.SourceCursor is null ? null : redactor.RedactText(log.SourceCursor, 512),
            TraceId = Identifier(log.TraceId, 32),
            SpanId = Identifier(log.SpanId, 16)
        };
    }

    /// <summary>
    ///     Rejects arbitrary metadata where only nonzero canonical hexadecimal trace identity is
    ///     valid.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <param name="length">The maximum permitted identifier or text length.</param>
    /// <returns>The canonical hexadecimal identifier, or null when absent or invalid.</returns>
    private static string? Identifier(string? value, int length)
    {
        return value is not null && value.Length == length &&
               value.Any(character => character != '0') && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : null;
    }
}