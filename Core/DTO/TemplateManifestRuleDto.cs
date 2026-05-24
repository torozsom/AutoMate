namespace Core.DTO;

/// <summary>
///     Represents a rule for generating a file based on a template, including the template file name,
///     the output file name, and whether the rule is active. This class is used to define how templates
///     should be processed and what output files should be generated during the templating process.
/// </summary>
public record TemplateManifestRuleDto
{
    /// <summary>
    ///     The name of the template rule, used for identification and reference within the application.
    /// </summary>
    public string TemplateFile { get; set; } = string.Empty;

    /// <summary>
    ///     The output file name that should be generated based on the template, indicating
    ///     the target file name for the generated content when the template is processed.
    /// </summary>
    public string OutputFile { get; set; } = string.Empty;

    /// <summary>
    ///     Indicates which deployment target should render this template.
    ///     Supported values are "All", "Local", and "Cloud".
    /// </summary>
    public string DeploymentTarget { get; set; } = "All";

    /// <summary>
    ///     A boolean flag indicating whether this template rule is active and should be applied during the templating process.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
