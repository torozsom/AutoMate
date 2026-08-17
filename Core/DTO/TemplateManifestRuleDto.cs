namespace Core.DTO;

/// <summary>
///     Represents one template manifest rule used to decide which deployment artifact should be rendered.
/// </summary>
public record TemplateManifestRuleDto
{
    /// <summary>
    ///     The template file name to load from the template catalog.
    /// </summary>
    public string TemplateFile { get; init; } = string.Empty;

    /// <summary>
    ///     The relative output path where the rendered template should be written.
    /// </summary>
    public string OutputFile { get; init; } = string.Empty;

    /// <summary>
    ///     Indicates which deployment target should render this template.
    ///     Supported values are "All", "Local", and "Cloud".
    /// </summary>
    public string DeploymentTarget { get; init; } = "All";

    /// <summary>
    ///     Indicates whether this template rule should be applied.
    /// </summary>
    public bool IsActive { get; init; } = true;
}