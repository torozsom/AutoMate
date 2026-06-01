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
        var generatedFiles = await GenerateAllTemplatesAsync(config, metadata, csProjectName, outputDirectory,
            cancellationToken);

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        foreach (var file in generatedFiles)
        {
            var outputPath = ResolveOutputPath(outputDirectory, file.Path);
            var outputDir = Path.GetDirectoryName(outputPath);

            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            await File.WriteAllTextAsync(outputPath, file.Content, cancellationToken);
            logger.LogInformation("[TemplateService] Generated file saved: {OutputPath}", outputPath);
        }
    }

    /// <inheritdoc />
    public async Task<List<TemplateFile>> GenerateAllTemplatesAsync(DeploymentConfigDto config,
        ProjectMetadataDto metadata,
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
            return [];
        }

        var unifiedModel = BuildTemplateModel(config, metadata, csProjectName, outputDirectory);
        var generatedFiles = new List<TemplateFile>();

        foreach (var rule in templates.Where(t => t.IsActive && ShouldRenderTemplate(t, config)))
        {
            var content = await RenderTemplateAsync(rule, unifiedModel, cancellationToken);
            if (content == null)
                continue;

            generatedFiles.Add(new TemplateFile
            {
                Path = NormalizeRelativeOutputPath(rule.OutputFile),
                Content = content
            });
        }

        return generatedFiles;
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
        var solutionRoot = ResolveTemplateRoot(outputDirectory);

        var projectListForTemplate = metadata.AllProjectPaths
            .Select(p =>
            {
                var relPath = Path.GetRelativePath(solutionRoot, p).Replace('\\', '/');
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
            : Path.GetRelativePath(solutionRoot, mainProjectFile).Replace('\\', '/');

        // Calculate the relative folder of the main project file for use in templates
        var relativeMainProjectFolder = string.IsNullOrEmpty(relativeMainProjectFile)
            ? "."
            : Path.GetDirectoryName(relativeMainProjectFile)?.Replace('\\', '/') ?? ".";

        // If the main project file is in the same directory as the output, set the relative folder to "."
        if (string.IsNullOrEmpty(relativeMainProjectFolder))
            relativeMainProjectFolder = ".";

        var normalizedAppName = NormalizeResourceName(config.ProjectName);
        var normalizedProjectName = NormalizeResourceName(csProjectName);

        var databasesForTemplate = config.Databases
            .Where(db => !string.IsNullOrWhiteSpace(db.DbType))
            .Select((db, index) =>
            {
                var type = NormalizeDatabaseType(db.DbType);
                var connectionName = string.IsNullOrWhiteSpace(db.ConnectionStringName)
                    ? $"Database{index + 1}Connection"
                    : db.ConnectionStringName.Trim();
                var databaseName = string.IsNullOrWhiteSpace(db.DbName)
                    ? $"appdb{index + 1}"
                    : db.DbName.Trim();
                var resourceSuffix = NormalizeResourceSegment($"{connectionName}-{index}", 18);
                var requiresLogin = RequiresDatabaseLogin(type);

                return new
                {
                    index,
                    type,
                    requires_login = requiresLogin,
                    name = databaseName,
                    user = db.DbUser,
                    password = db.DbPassword,
                    conn_name = connectionName,
                    connection_env_name = $"ConnectionStrings__{connectionName}",
                    container_suffix = string.IsNullOrWhiteSpace(db.ContainerNameSuffix)
                        ? $"db{index + 1}"
                        : db.ContainerNameSuffix.Trim(),
                    resource_suffix = resourceSuffix,
                    bicep_username_param = $"db{index}Username",
                    bicep_password_param = $"db{index}Password",
                    bicep_username_value = $"db{index}UsernameValue",
                    bicep_password_value = $"db{index}PasswordValue",
                    github_username_secret = CloudDeploymentSecretNames.GetDatabaseUsernameSecretName(index),
                    github_password_secret = CloudDeploymentSecretNames.GetDatabasePasswordSecretName(index),
                    connection_secret_name = NormalizeContainerAppSecretName($"db-{index}-{connectionName}-connection")
                };
            }).ToList();

        var containerAppName = string.IsNullOrWhiteSpace(config.CloudContainerAppName)
            ? normalizedAppName
            : NormalizeResourceName(config.CloudContainerAppName);

        var resourceGroupName = string.IsNullOrWhiteSpace(config.CloudResourceGroupName)
            ? $"rg-{containerAppName}"
            : config.CloudResourceGroupName.Trim();

        var registryName = string.IsNullOrWhiteSpace(config.CloudRegistryName)
            ? "ghcr.io"
            : config.CloudRegistryName.Trim();

        return new
        {
            // Metadata for the Dockerfile
            app_name = config.ProjectName,
            project_name = csProjectName,
            app_slug = normalizedAppName,
            project_slug = normalizedProjectName,

            dotnet_version = metadata.DotNetVersion,
            projects = projectListForTemplate,
            main_project_relative_path = relativeMainProjectFile,
            main_project_folder = relativeMainProjectFolder,

            // Configurations for the docker-compose file
            exposed_port = config.ExposedPort,
            environment_name = config.EnvironmentName,
            requires_db = databasesForTemplate.Count > 0,
            databases = databasesForTemplate,

            // Cloud deployment settings
            is_cloud_deployment = config.IsCloudDeployment,
            azure_location = config.CloudAzureRegion,
            resource_group_name = resourceGroupName,
            container_app_name = containerAppName,
            container_environment_name = $"{containerAppName}-env",
            log_analytics_workspace_name = $"{containerAppName}-logs",
            managed_identity_name = $"{containerAppName}-identity",
            registry_server = registryName,
            image_name = normalizedAppName,

            // Custom environment variables as a list of key-value pairs for iteration in templates
            custom_env_vars = config.CustomEnvVars?
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select((kvp, index) => new
                {
                    index,
                    key = kvp.Key.Trim(),
                    value = kvp.Value,
                    bicep_value_param = $"customEnv{index}Value",
                    bicep_decoded_value = $"customEnv{index}DecodedValue",
                    github_value_secret = CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(index,
                        kvp.Key.Trim()),
                    container_secret_name = NormalizeContainerAppSecretName($"env-{index}-{kvp.Key}")
                })
                .ToList() ?? []
        };
    }


    private static string ResolveTemplateRoot(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || outputDirectory == ".")
            return Directory.GetCurrentDirectory();

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        return string.Equals(Path.GetFileName(fullOutputDirectory), ".automate", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(fullOutputDirectory) ?? fullOutputDirectory
            : fullOutputDirectory;
    }


    /// <summary>
    ///     Determines whether a manifest rule should be rendered for the selected deployment target.
    /// </summary>
    /// <param name="rule">The template manifest rule.</param>
    /// <param name="config">The active deployment configuration.</param>
    private static bool ShouldRenderTemplate(TemplateManifestRuleDto rule, DeploymentConfigDto config)
    {
        var target = string.IsNullOrWhiteSpace(rule.DeploymentTarget)
            ? "All"
            : rule.DeploymentTarget.Trim();

        if (target.Equals("All", StringComparison.OrdinalIgnoreCase))
            return true;

        if (config.IsCloudDeployment)
            return target.Equals("Cloud", StringComparison.OrdinalIgnoreCase);

        return target.Equals("Local", StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    ///     Creates a conservative Azure- and image-friendly name from user supplied project names.
    /// </summary>
    /// <param name="value">The source value.</param>
    private static string NormalizeResourceName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);

        normalized = normalized.Trim('-');

        return string.IsNullOrWhiteSpace(normalized) ? "automate-app" : normalized;
    }


    private static string NormalizeDatabaseType(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => "PostgreSQL",
            "mysql" => "MySQL",
            "sqlserver" or "sql-server" or "mssql" or "microsoft sql server" => "SQLServer",
            "mongodb" or "mongo" => "MongoDB",
            "redis" => "Redis",
            _ => value.Trim()
        };
    }


    private static bool RequiresDatabaseLogin(string databaseType)
    {
        return databaseType is "PostgreSQL" or "MySQL" or "SQLServer";
    }


    private static string NormalizeResourceSegment(string value, int maxLength)
    {
        var normalized = NormalizeResourceName(value);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd('-');
    }


    private static string NormalizeContainerAppSecretName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "automate-secret";

        return normalized.Length <= 63 ? normalized : normalized[..63].TrimEnd('-');
    }


    /// <summary>
    ///     Renders a Scriban template based on the provided rule and unified model.
    /// </summary>
    /// <param name="rule">
    ///     The template manifest rule containing the template file name, output file name,
    ///     and an active flag indicating whether the template should be processed.
    /// </param>
    /// <param name="unifiedModel">
    ///     The unified view-model object that combines deployment configuration
    ///     and project metadata, used for rendering the Scriban template.
    /// </param>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the template generation process.
    /// </param>
    /// <returns>The rendered template content, or null when the template file is missing.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Throws InvalidOperationException if the specified template file cannot be parsed or rendered successfully,
    ///     including details about the parsing errors encountered in the Scriban template.
    /// </exception>
    private async Task<string?> RenderTemplateAsync(TemplateManifestRuleDto rule, object unifiedModel,
        CancellationToken cancellationToken)
    {
        var templatePath = ResolveTemplatePath(rule.TemplateFile);

        if (!File.Exists(templatePath))
        {
            logger.LogWarning("[TemplateService] The template file '{TemplateFile}' does not exist!",
                rule.TemplateFile);
            return null;
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
            logger.LogInformation("[TemplateService] Successfully rendered: {OutputFile}", rule.OutputFile);
            return renderedContent;
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


    private static string ResolveTemplatePath(string templateFile)
    {
        var templatePath = Path.GetFullPath(Path.Combine(TemplatesDirectory, templateFile));
        var templatesRoot = Path.GetFullPath(TemplatesDirectory);

        if (!IsPathUnderRoot(templatePath, templatesRoot))
            throw new InvalidOperationException($"Template path escapes the templates directory: {templateFile}");

        return templatePath;
    }


    private static string ResolveOutputPath(string outputDirectory, string relativePath)
    {
        var outputRoot = Path.GetFullPath(outputDirectory);
        var outputPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));

        if (!IsPathUnderRoot(outputPath, outputRoot))
            throw new InvalidOperationException($"Generated output path escapes the target directory: {relativePath}");

        return outputPath;
    }


    private static string NormalizeRelativeOutputPath(string outputFile)
    {
        if (string.IsNullOrWhiteSpace(outputFile))
            throw new InvalidOperationException("Template manifest contains an empty output file path.");

        if (Path.IsPathRooted(outputFile))
            throw new InvalidOperationException($"Template output path must be relative: {outputFile}");

        var normalized = outputFile.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidOperationException($"Template output path cannot contain parent traversal: {outputFile}");

        return normalized;
    }


    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
