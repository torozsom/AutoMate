using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Logging;
using Scriban;

namespace Services.Templating;

/// <summary>
///     Provides functionality for generating templated files such as Dockerfile and .dockerignore
///     based on predefined Scriban templates and for saving generated files to a specified directory.
/// </summary>
public class TemplatingService(ILogger<TemplatingService> logger) : ITemplatingService
{
    private static List<TemplateManifestRuleDto>? _cachedManifest;
    private static readonly SemaphoreSlim ManifestSemaphore = new(1, 1);

    private static readonly string TemplatesDirectory
        = Path.Combine(AppContext.BaseDirectory, "Templating", "Templates");


    /// <inheritdoc />
    public async Task GenerateAndSaveAllTemplatesAsync(DeploymentConfigDto config, ProjectMetadataDto metadata,
        string csProjectName, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(TemplatesDirectory))
        {
            logger.LogCritical("[TemplateService] Templates directory not found at: {TemplatesDirectory}",
                TemplatesDirectory);
            throw new DirectoryNotFoundException($"Templates directory not found at: {TemplatesDirectory}");
        }

        var templates = await GetTemplateManifestAsync(cancellationToken);

        if (templates == null || templates.Count == 0)
        {
            logger.LogWarning(
                "[TemplateService] Template manifest is empty or could not be loaded. No templates will be generated.");
            return;
        }

        // Build the unified view-model for Scriban
        var unifiedModel = BuildTemplateModel(config, metadata, csProjectName, outputDirectory);

        // Ensure the output directory exists
        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        foreach (var rule in templates.Where(t => t.IsActive))
            await RenderAndSaveTemplateAsync(rule, unifiedModel, outputDirectory, cancellationToken);
    }


    /// <summary>
    ///     Asynchronously retrieves the template manifest, which contains a list of template rules.
    ///     If the manifest has been previously loaded and cached, the cached copy will be returned.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A list of template manifest rules, or null if an error occurs during retrieval or parsing.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the template manifest file is not found.</exception>
    private async Task<List<TemplateManifestRuleDto>?> GetTemplateManifestAsync(CancellationToken cancellationToken)
    {
        if (_cachedManifest != null) return _cachedManifest;

        var manifestPath = Path.Combine(TemplatesDirectory, "template-manifest.json");

        if (!File.Exists(manifestPath))
        {
            logger.LogError("[TemplateService] Template manifest not found at: {ManifestPath}", manifestPath);
            throw new FileNotFoundException($"Template manifest not found: {manifestPath}");
        }

        await ManifestSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedManifest != null)
                return _cachedManifest;

            await using var stream =
                new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            _cachedManifest =
                await JsonSerializer.DeserializeAsync<List<TemplateManifestRuleDto>>(stream,
                    cancellationToken: cancellationToken);

            return _cachedManifest;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("[TemplateService] Template manifest loading cancelled. Exception: {Exception}",
                ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TemplateService] Failed to parse template-manifest.json");
            return null;
        }
        finally
        {
            ManifestSemaphore.Release();
        }
    }


    /// <summary>
    ///     Builds a unified view-model object that combines deployment configuration
    ///     and project metadata, which is then used for rendering Scriban templates.
    /// </summary>
    /// <param name="config">
    ///     The deployment configuration containing settings such as exposed port,
    ///     environment name, database requirements, and custom environment variables.
    /// </param>
    /// <param name="metadata">
    ///     The project metadata containing information about the .NET version, project paths,
    ///     and other relevant details needed for template generation.
    /// </param>
    /// <param name="csProjectName">
    ///     The name of the main C# project (without .csproj extension) which is used to
    ///     identify the main project file and its relative path for template generation.
    /// </param>
    /// <param name="outputDirectory">
    ///     The directory where the generated templated files will be saved,
    ///     used to calculate relative paths for projects in the template model.
    /// </param>
    /// <returns>
    ///     An object containing combined properties from both deployment configuration and project metadata,
    ///     structured in a way that is suitable for use as a model in Scriban template rendering.
    /// </returns>
    private static object BuildTemplateModel(DeploymentConfigDto config, ProjectMetadataDto metadata,
        string csProjectName, string outputDirectory)
    {
        var solutionRoot = Path.GetDirectoryName(outputDirectory);

        var projectListForTemplate = metadata.AllProjectPaths
            .Select(p =>
            {
                var relPath = Path.GetRelativePath(solutionRoot!, p).Replace('\\', '/');
                var dir = Path.GetDirectoryName(relPath)?.Replace('\\', '/');
                return new
                {
                    relative_path = relPath,
                    folder = string.IsNullOrEmpty(dir) ? "." : dir
                };
            }).ToList();

        // Determine the main project file and its relative path for use in templates
        var mainProjectFile = metadata.AllProjectPaths
            .FirstOrDefault(p => p.EndsWith($"{csProjectName}.csproj", StringComparison.OrdinalIgnoreCase));

        // Calculate the relative path to the main project file from the output directory
        var relativeMainProjectFile = string.IsNullOrEmpty(mainProjectFile)
            ? ""
            : Path.GetRelativePath(solutionRoot!, mainProjectFile).Replace('\\', '/');

        // Calculate the relative folder of the main project file for use in templates
        var relativeMainProjectFolder = string.IsNullOrEmpty(relativeMainProjectFile)
            ? "."
            : Path.GetDirectoryName(relativeMainProjectFile)?.Replace('\\', '/') ?? ".";

        // If the main project file is in the same directory as the output, set the relative folder to "."
        if (string.IsNullOrEmpty(relativeMainProjectFolder))
            relativeMainProjectFolder = ".";

        var databasesForTemplate = config.Databases.Select(db => new
        {
            type = db.DbType,
            name = db.DbName,
            user = db.DbUser,
            password = db.DbPassword,

            user_encoded = string.IsNullOrEmpty(db.DbUser) ? "" : Uri.EscapeDataString(db.DbUser),
            password_encoded = string.IsNullOrEmpty(db.DbPassword) ? "" : Uri.EscapeDataString(db.DbPassword),

            conn_name = db.ConnectionStringName,
            container_suffix = db.ContainerNameSuffix
        }).ToList();

        return new
        {
            // Metadata for the Dockerfile
            app_name = config.ProjectName,
            project_name = csProjectName,

            dotnet_version = metadata.DotNetVersion,
            projects = projectListForTemplate,
            main_project_relative_path = relativeMainProjectFile,
            main_project_folder = relativeMainProjectFolder,

            // Configurations for the docker-compose file
            exposed_port = config.ExposedPort,
            environment_name = config.EnvironmentName,
            requires_db = databasesForTemplate.Count > 0,
            databases = databasesForTemplate,

            // Custom environment variables as a list of key-value pairs for iteration in templates
            custom_env_vars = config.CustomEnvVars?
                .Select(kvp => new { key = kvp.Key, value = kvp.Value })
                .ToList() ?? []
        };
    }


    /// <summary>
    ///     Renders a Scriban template based on the provided rule and unified model,
    ///     and saves the generated content to the specified output directory.
    /// </summary>
    /// <param name="rule">
    ///     The template manifest rule containing the template file name, output file name,
    ///     and an active flag indicating whether the template should be processed.
    /// </param>
    /// <param name="unifiedModel">
    ///     The unified view-model object that combines deployment configuration
    ///     and project metadata, used for rendering the Scriban template.
    /// </param>
    /// <param name="outputDirectory">
    ///     The directory where the generated templated file will be saved, used to calculate relative paths
    ///     for projects in the template model and to determine the destination path for the generated file.
    /// </param>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the template generation process.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     Throws InvalidOperationException if the specified template file cannot be parsed or rendered successfully,
    ///     including details about the parsing errors encountered in the Scriban template.
    /// </exception>
    private async Task RenderAndSaveTemplateAsync(TemplateManifestRuleDto rule, object unifiedModel,
        string outputDirectory, CancellationToken cancellationToken)
    {
        var templatePath = Path.Combine(TemplatesDirectory, rule.TemplateFile);

        if (!File.Exists(templatePath))
        {
            logger.LogWarning("[TemplateService] The template file '{TemplateFile}' does not exist!",
                rule.TemplateFile);
            return;
        }

        try
        {
            var templateText = await File.ReadAllTextAsync(templatePath, cancellationToken);
            var parsedTemplate = Template.Parse(templateText);

            if (parsedTemplate.HasErrors)
            {
                var errors = string.Join(", ", parsedTemplate.Messages.Select(m => m.Message));
                logger.LogError("[TemplateService] Could not parse Scriban template: {TemplateFile}. Errors: {Errors}",
                    rule.TemplateFile, errors);
                throw new InvalidOperationException($"Could not parse file: {rule.TemplateFile}, errors: {errors}");
            }

            // Render the template with the unified model
            var renderedContent = await parsedTemplate.RenderAsync(unifiedModel);

            // Save the rendered content to the specified output directory
            var destinationPath = Path.Combine(outputDirectory, rule.OutputFile);
            await File.WriteAllTextAsync(destinationPath, renderedContent, cancellationToken);

            logger.LogInformation("[TemplateService] Successfully generated: {OutputFile}", rule.OutputFile);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[TemplateService] Template generation cancelled for '{OutputFile}'.", rule.OutputFile);
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[TemplateService] Unexpected error occurred while generating template: {TemplateFile}",
                rule.TemplateFile);
            throw;
        }
    }
}