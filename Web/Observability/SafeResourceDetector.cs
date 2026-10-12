using Application.Abstractions.Diagnostics;
using OpenTelemetry.Resources;

namespace Web.Observability;

/// <summary>
///     Builds only reviewed resource fields; environment detectors cannot import arbitrary
///     attributes.
/// </summary>
/// <param name="redactor">Masks credentials and unsafe diagnostic text.</param>
/// <param name="service">Operator-configured service label.</param>
/// <param name="environment">Operator-configured environment label.</param>
/// <param name="version">Operator-configured service version.</param>
/// <param name="profile">Selected hosting profile label.</param>
public sealed class SafeResourceDetector(
    IDiagnosticRedactor redactor,
    string service,
    string environment,
    string version,
    string profile) : IResourceDetector
{
    /// <summary>
    ///     Builds an SDK resource containing only reviewed operator labels.
    /// </summary>
    /// <returns>An SDK resource with reviewed service, version, environment and profile labels.</returns>
    public Resource Detect()
    {
        return new Resource(new Dictionary<string, object>
        {
            ["service.name"] = Label(service, "AutoMate"),
            ["service.version"] = Label(version, "unknown"),
            ["deployment.environment"] = Label(environment, "unknown"),
            ["automate.hosting_profile"] = profile == "SaaS" ? "SaaS" : "SelfHosted"
        });
    }

    /// <summary>
    ///     Accepts bounded operator labels after masking, rejecting URLs, assignments and control
    ///     characters.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <param name="fallback">Fixed label used when the supplied value is unsafe.</param>
    /// <returns>The unchanged safe label, or the supplied fixed fallback.</returns>
    private string Label(string value, string fallback)
    {
        return value.Length is > 0 and <= 64 &&
               value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') &&
               redactor.RedactText(value, 64) == value
            ? value
            : fallback;
    }
}