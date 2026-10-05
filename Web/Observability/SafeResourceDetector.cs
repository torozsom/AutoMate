using Application.Abstractions.Diagnostics;
using OpenTelemetry.Resources;

namespace Web.Observability;

/// <summary>Builds only reviewed resource fields; environment detectors cannot import arbitrary attributes.</summary>
public sealed class SafeResourceDetector(
    IDiagnosticRedactor redactor,
    string service,
    string environment,
    string version,
    string profile) : IResourceDetector
{
    /// <inheritdoc />
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

    /// <summary>Accepts bounded operator labels after masking, rejecting URLs, assignments and control characters.</summary>
    private string Label(string value, string fallback)
    {
        return value.Length is > 0 and <= 64 &&
               value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') &&
               redactor.RedactText(value, 64) == value
            ? value
            : fallback;
    }
}