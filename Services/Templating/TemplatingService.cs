using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Templating;

/// <summary>
///     Coordinates Scriban template generation for local Docker and cloud deployment assets.
/// </summary>
public sealed class TemplatingService(ILogger<TemplatingService> logger) : ITemplatingService
{
    /// <summary>
    ///     Loads and caches template manifest rules from the template directory.
    /// </summary>
    private readonly TemplateManifestCatalog _manifestCatalog = new(logger);

    /// <summary>
    ///     Writes generated template files to disk.
    /// </summary>
    private readonly TemplateFileWriter _templateFileWriter = new(logger);

    /// <summary>
    ///     Renders individual Scriban templates.
    /// </summary>
    private readonly ScribanTemplateRenderer _templateRenderer = new(logger);

    /// <inheritdoc />
    public async Task GenerateAndSaveAllTemplatesAsync(DeploymentConfigDto config, ProjectMetadataDto metadata,
        string csProjectName, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var generatedFiles = await GenerateAllTemplatesAsync(config, metadata, csProjectName, outputDirectory,
            cancellationToken);

        await _templateFileWriter.WriteAllAsync(outputDirectory, generatedFiles, cancellationToken);
    }

    /// <inheritdoc />
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