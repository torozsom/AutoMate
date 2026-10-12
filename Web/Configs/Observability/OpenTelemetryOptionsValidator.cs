using Microsoft.Extensions.Options;

namespace Web.Configs;

/// <summary>
///     Validates optional exporter configuration without exposing endpoint or resource-label values.
/// </summary>
public sealed class OpenTelemetryOptionsValidator : IValidateOptions<OpenTelemetryOptions>
{
    /// <summary>
    ///     Validates optional exporter settings and returns fixed setting-specific failures.
    /// </summary>
    /// <param name="name">Metric or configuration name to resolve.</param>
    /// <param name="options">Bound telemetry exporter settings to validate.</param>
    /// <returns>Success when exporters are disabled or settings are valid; otherwise fixed validation failures.</returns>
    public ValidateOptionsResult Validate(string? name, OpenTelemetryOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ServiceName) || options.ServiceName.Length > 100)
            failures.Add("OpenTelemetry:ServiceName must contain 1–100 nonblank characters.");
        if (options.Environment is not null &&
            (string.IsNullOrWhiteSpace(options.Environment) || options.Environment.Length > 100))
            failures.Add("OpenTelemetry:Environment must be omitted or contain 1–100 nonblank characters.");
        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint) && !TryGetCollectorEndpoint(options.OtlpEndpoint, out _))
            failures.Add(
                "OpenTelemetry:OtlpEndpoint must be an absolute HTTP(S) collector URL without credentials, query or fragment.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    ///     Shared registration/validation guard; absent or invalid settings never configure an exporter.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <param name="endpoint">Parsed HTTP(S) collector endpoint, or null when invalid or absent.</param>
    /// <returns>True when a present endpoint satisfies the supported URI and transport policy; otherwise false.</returns>
    internal static bool TryGetCollectorEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(character =>
                char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment)) return false;
        endpoint = parsed;
        return true;
    }
}