using Application.Abstractions.Templating;
using Domain.DTO;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Templating;

/// <summary>
///     Coordinates Scriban template generation for local Docker and cloud deployment assets.
/// </summary>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class TemplatingService(ILogger<TemplatingService> logger) : ITemplatingService
{
    /// <summary>Loads and caches template manifest rules from the template directory.</summary>
    private readonly TemplateManifestCatalog _manifestCatalog = new(logger);

    /// <summary>Writes generated template files to disk.</summary>
    private readonly TemplateFileWriter _templateFileWriter = new(logger);

    /// <summary>Renders individual Scriban templates.</summary>
    private readonly ScribanTemplateRenderer _templateRenderer = new(logger);

    /// <summary>
    ///     Generates all necessary templates and saves them to the specified output directory.
    /// </summary>
    /// <param name="config">The deployment configuration.</param>
    /// <param name="metadata">The project metadata.</param>
    /// <param name="csProjectName">The name of the main C# project.</param>
    /// <param name="outputDirectory">The target directory for the generated files.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that completes after the selected templates are rendered and saved.</returns>
    /// <exception cref="T:System.IO.DirectoryNotFoundException">
    ///     The configured templates directory does not exist.
    /// </exception>
    /// <exception cref="T:System.IO.FileNotFoundException">
    ///     The required template manifest is missing.
    /// </exception>
    public async Task GenerateAndSaveAllTemplatesAsync(DeploymentConfigDto config, ProjectMetadataDto metadata,
        string csProjectName, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var generatedFiles = await GenerateAllTemplatesAsync(config, metadata, csProjectName, outputDirectory,
            cancellationToken);

        await _templateFileWriter.WriteAllAsync(outputDirectory, generatedFiles, cancellationToken);
    }

    /// <summary>
    ///     Generates all matching templates and returns their content without saving them to disk.
    /// </summary>
    /// <param name="config">The deployment configuration.</param>
    /// <param name="metadata">The project metadata.</param>
    /// <param name="csProjectName">The name of the main C# project.</param>
    /// <param name="outputDirectory">
    ///     The directory used as the relative-path anchor while rendering templates.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task returning a list of generated template files with relative output paths and
    ///     content.
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">
    ///     The configured deployment templates directory does not exist.
    /// </exception>
    /// <exception cref="T:System.IO.FileNotFoundException">
    ///     The required template manifest is missing.
    /// </exception>
    public async Task<List<TemplateFile>> GenerateAllTemplatesAsync(DeploymentConfigDto config,
        ProjectMetadataDto metadata,
        string csProjectName, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(TemplatePaths.TemplatesDirectory))
        {
            logger.LogCritical("[TemplateService] Templates directory not found at: {TemplatesDirectory}",
                TemplatePaths.TemplatesDirectory);
            throw new DirectoryNotFoundException(
                $"Templates directory not found at: {TemplatePaths.TemplatesDirectory}");
        }

        var templates = await _manifestCatalog.GetAsync(cancellationToken);
        if (templates.Count == 0)
        {
            logger.LogWarning(
                "[TemplateService] Template manifest is empty or could not be loaded. No templates will be generated.");
            return [];
        }

        var model = TemplateModelFactory.Create(config, metadata, csProjectName, outputDirectory);
        var generatedFiles = new List<TemplateFile>();

        foreach (var rule in templates.Where(rule => TemplateRuleMatcher.ShouldRender(rule, config)))
        {
            var content = await _templateRenderer.RenderAsync(rule, model, cancellationToken);
            if (content == null)
                continue;

            generatedFiles.Add(new TemplateFile
            {
                Path = TemplatePaths.NormalizeRelativeOutputPath(rule.OutputFile),
                Content = content
            });
        }

        return generatedFiles;
    }
}