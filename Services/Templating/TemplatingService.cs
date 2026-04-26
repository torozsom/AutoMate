using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Logging;
using Scriban;

namespace Services.Templating;

/// <summary>
///     Provides functionality for generating templated files such as Dockerfile and .dockerignore
///     based on predefined Scriban templates and for saving generated files to a specified directory.
/// </summary>
public class TemplateService : ITemplateService
{
    private static readonly string TemplatesDirectory
        = Path.Combine(AppContext.BaseDirectory, "Templating", "Templates");

    public TemplateService(ILogger<TemplateService> logger)
    {
        if (Directory.Exists(TemplatesDirectory)) return;

        logger.LogError("Templates directory not found at: {TemplatesDirectory}", TemplatesDirectory);
        throw new DirectoryNotFoundException($"Templates directory not found at: {TemplatesDirectory}");
    }


    /// <summary>
    /// </summary>
    /// <param name="config"></param>
    /// <param name="metadata"></param>
    /// <param name="csProjectName"></param>
    /// <param name="outputDirectory"></param>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task GenerateAndSaveAllTemplatesAsync(DeploymentConfigDto config, ProjectMetadataDto metadata,
        string csProjectName, string outputDirectory)
    {
        // Load the template manifest which defines which templates to process and their output file names
        var manifestPath = Path.Combine(TemplatesDirectory, "template-manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Template manifest nem található: {manifestPath}");

        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        var templates = JsonSerializer.Deserialize<List<TemplateManifestRuleDto>>(manifestContent);

        if (templates == null || templates.Count == 0) return;

        var projectListForTemplate = metadata.AllProjectPaths
            .Select(p =>
            {
                var relPath = Path.GetRelativePath(outputDirectory, p).Replace('\\', '/');
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
            : Path.GetRelativePath(outputDirectory, mainProjectFile).Replace('\\', '/');

        // Calculate the relative folder of the main project file for use in templates
        var relativeMainProjectFolder = string.IsNullOrEmpty(relativeMainProjectFile)
            ? ""
            : Path.GetDirectoryName(relativeMainProjectFile)?.Replace('\\', '/') ?? "";

        // If the main project file is in the same directory as the output, set the relative folder to "."
        if (string.IsNullOrEmpty(relativeMainProjectFolder))
            relativeMainProjectFolder = ".";

        // Create a unified model that combines both metadata and configuration for use in templates
        var unifiedModel = new
        {
            // Metadata for the Dockerfile
            dotnet_version = metadata.DotNetVersion,
            project_name = csProjectName,
            projects = projectListForTemplate,
            main_project_relative_path = relativeMainProjectFile,
            main_project_folder = relativeMainProjectFolder,

            // Configurations for the docker-compose file
            exposed_port = config.ExposedPort,
            environment_name = config.EnvironmentName,
            requires_db = config.RequiresDb,
            db_type = config.DbType,
            db_name = config.DbName,
            db_user = config.DbUser,
            db_password = config.DbPassword,

            // URL-encoded variables for safe inclusion in connection strings or environment variables
            db_user_encoded = string.IsNullOrEmpty(config.DbUser) ? "" : Uri.EscapeDataString(config.DbUser),
            db_password_encoded =
                string.IsNullOrEmpty(config.DbPassword) ? "" : Uri.EscapeDataString(config.DbPassword),

            // Custom environment variables as a list of key-value pairs for iteration in templates
            custom_env_vars = config.CustomEnvVars?
                .Select(object (kvp) => new { key = kvp.Key, value = kvp.Value })
                .ToList() ?? []
        };

        // Iterate through active templates and generate files
        foreach (var rule in templates.Where(t => t.IsActive))
        {
            var templatePath = Path.Combine(TemplatesDirectory, rule.TemplateFile);
            if (!File.Exists(templatePath))
            {
                Console.WriteLine($"[WARNING] The template file: {rule.TemplateFile} does not exist!");
                continue;
            }

            var templateText = await File.ReadAllTextAsync(templatePath);
            var parsedTemplate = Template.Parse(templateText);

            if (parsedTemplate.HasErrors)
            {
                var errors = string.Join(", ", parsedTemplate.Messages.Select(m => m.Message));
                await Console.Error.WriteLineAsync(errors);
                throw new InvalidOperationException($"Could not parse file: {rule.TemplateFile}, errors: {errors}");
            }

            // Render the template with the unified model
            var renderedContent = await parsedTemplate.RenderAsync(unifiedModel);

            // Save the rendered content to the specified output directory
            var destinationPath = Path.Combine(outputDirectory, rule.OutputFile);
            await File.WriteAllTextAsync(destinationPath, renderedContent);

            Console.WriteLine($"[TEMPLATE ENGINE] Successfully generated: {rule.OutputFile}");
        }
    }
}