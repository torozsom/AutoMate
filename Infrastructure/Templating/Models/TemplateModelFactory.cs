using Domain.DTO.Deployment;
using Domain.DTO.Projects;
using Infrastructure.Templating.Catalog;

namespace Infrastructure.Templating.Models;

/// <summary>
///     Builds the anonymous Scriban model consumed by AutoMate deployment templates.
/// </summary>
internal static class TemplateModelFactory
{
    /// <summary>
    ///     Creates the unified model combining deployment configuration and project metadata.
    /// </summary>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="metadata">
    ///     The non-secret project metadata supplied to evidence or artifact generation.
    /// </param>
    /// <param name="csProjectName">The selected C# project name used for generated artifacts.</param>
    /// <param name="outputDirectory">The destination directory for generated deployment artifacts.</param>
    /// <returns>The bounded projection of the documented configuration or status fields.</returns>
    public static object Create(DeploymentConfigDto config, ProjectMetadataDto metadata, string csProjectName,
        string outputDirectory)
    {
        var solutionRoot = TemplatePaths.ResolveTemplateRoot(outputDirectory);
        var projectListForTemplate = CreateProjectList(metadata, solutionRoot);
        var mainProject = ResolveMainProject(metadata, csProjectName, solutionRoot);
        var normalizedAppName = TemplateNameNormalizer.NormalizeResourceName(config.ProjectName);
        var normalizedProjectName = TemplateNameNormalizer.NormalizeResourceName(csProjectName);
        var databasesForTemplate = CreateDatabaseList(config);
        var cloudNames = CreateCloudNames(config, normalizedAppName);

        return new
        {
            // Dockerfile metadata
            app_name = config.ProjectName,
            project_name = csProjectName,
            app_slug = normalizedAppName,
            project_slug = normalizedProjectName,
            dotnet_version = metadata.DotNetVersion,
            projects = projectListForTemplate,
            main_project_relative_path = mainProject.RelativePath,
            main_project_folder = mainProject.Folder,

            // docker-compose configuration
            automate_project_id = config.ProjectId.ToString("N"),
            exposed_port = config.ExposedPort,
            environment_name = config.EnvironmentName,
            requires_db = databasesForTemplate.Count > 0,
            databases = databasesForTemplate,

            // Cloud deployment settings
            is_cloud_deployment = config.IsCloudDeployment,
            azure_location = config.CloudAzureRegion,
            resource_group_name = cloudNames.ResourceGroupName,
            container_app_name = cloudNames.ContainerAppName,
            container_environment_name = $"{cloudNames.ContainerAppName}-env",
            log_analytics_workspace_name = $"{cloudNames.ContainerAppName}-logs",
            managed_identity_name = $"{cloudNames.ContainerAppName}-identity",
            registry_server = cloudNames.RegistryName,
            use_acr = cloudNames.RegistryName.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase),
            registry_name = cloudNames.RegistryName.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase)
                ? cloudNames.RegistryName[..^".azurecr.io".Length]
                : cloudNames.RegistryName,
            image_name = normalizedAppName,

            // Custom environment variables are ordered for stable generated output.
            custom_env_vars = CreateCustomEnvironmentList(config)
        };
    }

    /// <summary>
    ///     Creates template project entries with paths relative to the template root.
    /// </summary>
    /// <param name="metadata">
    ///     The non-secret project metadata supplied to evidence or artifact generation.
    /// </param>
    /// <param name="solutionRoot">Resolved local solution/repository root.</param>
    /// <returns>The project list selection with its documented bounds and ordering.</returns>
    private static List<object> CreateProjectList(ProjectMetadataDto metadata, string solutionRoot)
    {
        return
        [
            .. metadata.AllProjectPaths
                .Select(projectPath =>
                {
                    var relativePath = Path.GetRelativePath(solutionRoot, projectPath).Replace('\\', '/');
                    var folder = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
                    return (object)new
                    {
                        relative_path = relativePath,
                        folder = string.IsNullOrEmpty(folder) ? "." : folder
                    };
                })
        ];
    }

    /// <summary>
    ///     Resolves the main project file and folder values expected by Dockerfile templates.
    /// </summary>
    /// <param name="metadata">
    ///     The non-secret project metadata supplied to evidence or artifact generation.
    /// </param>
    /// <param name="csProjectName">The selected C# project name used for generated artifacts.</param>
    /// <param name="solutionRoot">Resolved local solution/repository root.</param>
    /// <returns>Resolved main-project paths and runtime fields for template rendering.</returns>
    private static MainProjectTemplateInfo ResolveMainProject(ProjectMetadataDto metadata, string csProjectName,
        string solutionRoot)
    {
        var mainProjectFile = metadata.AllProjectPaths
            .FirstOrDefault(path => path.EndsWith($"{csProjectName}.csproj", StringComparison.OrdinalIgnoreCase));

        var relativeMainProjectFile = string.IsNullOrEmpty(mainProjectFile)
            ? string.Empty
            : Path.GetRelativePath(solutionRoot, mainProjectFile).Replace('\\', '/');

        var relativeMainProjectFolder = string.IsNullOrEmpty(relativeMainProjectFile)
            ? "."
            : Path.GetDirectoryName(relativeMainProjectFile)?.Replace('\\', '/') ?? ".";

        if (string.IsNullOrEmpty(relativeMainProjectFolder))
            relativeMainProjectFolder = ".";

        return new MainProjectTemplateInfo(relativeMainProjectFile, relativeMainProjectFolder);
    }

    /// <summary>
    ///     Creates template database entries from deployment database configuration.
    /// </summary>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <returns>The database list selection with its documented bounds and ordering.</returns>
    private static List<object> CreateDatabaseList(DeploymentConfigDto config)
    {
        return
        [
            .. config.Databases
                .Where(database => !string.IsNullOrWhiteSpace(database.DbType))
                .Select((database, index) => (object)CreateDatabaseTemplateModel(database, index))
        ];
    }

    /// <summary>
    ///     Creates the anonymous template model for one configured database.
    /// </summary>
    /// <param name="database">Selected owned database resource or deployment configuration.</param>
    /// <param name="index">Validated numeric position or durable lookup identity.</param>
    /// <returns>The bounded projection of the documented configuration or status fields.</returns>
    private static object CreateDatabaseTemplateModel(DatabaseConfigDto database, int index)
    {
        var type = TemplateNameNormalizer.NormalizeDatabaseType(database.DbType);
        var connectionName = string.IsNullOrWhiteSpace(database.ConnectionStringName)
            ? $"Database{index + 1}Connection"
            : database.ConnectionStringName.Trim();
        var databaseName = string.IsNullOrWhiteSpace(database.DbName)
            ? $"appdb{index + 1}"
            : database.DbName.Trim();
        var resourceSuffix = TemplateNameNormalizer.NormalizeResourceSegment($"{connectionName}-{index}", 18);
        var requiresLogin = TemplateNameNormalizer.RequiresDatabaseLogin(type);

        return new
        {
            index,
            type,
            requires_login = requiresLogin,
            name = databaseName,
            user = database.DbUser,
            password = database.DbPassword,
            user_encoded = string.IsNullOrEmpty(database.DbUser) ? string.Empty : Uri.EscapeDataString(database.DbUser),
            password_encoded = string.IsNullOrEmpty(database.DbPassword)
                ? string.Empty
                : Uri.EscapeDataString(database.DbPassword),
            conn_name = connectionName,
            connection_env_name = $"ConnectionStrings__{connectionName}",
            container_suffix = string.IsNullOrWhiteSpace(database.ContainerNameSuffix)
                ? $"db{index + 1}"
                : database.ContainerNameSuffix.Trim(),
            resource_suffix = resourceSuffix,
            bicep_username_param = $"db{index}Username",
            bicep_password_param = $"db{index}Password",
            bicep_username_value = $"db{index}UsernameValue",
            bicep_password_value = $"db{index}PasswordValue",
            github_username_secret = CloudDeploymentSecretNames.GetDatabaseUsernameSecretName(index),
            github_password_secret = CloudDeploymentSecretNames.GetDatabasePasswordSecretName(index),
            connection_secret_name = TemplateNameNormalizer.NormalizeContainerAppSecretName(
                $"db-{index}-{connectionName}-connection")
        };
    }

    /// <summary>
    ///     Resolves cloud resource names with the same fallbacks used before refactoring.
    /// </summary>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="normalizedAppName">Validated normalized application artifact name.</param>
    /// <returns>Normalized cloud resource names used by generated assets.</returns>
    private static CloudNameTemplateInfo CreateCloudNames(DeploymentConfigDto config, string normalizedAppName)
    {
        var containerAppName = string.IsNullOrWhiteSpace(config.CloudContainerAppName)
            ? normalizedAppName
            : TemplateNameNormalizer.NormalizeResourceName(config.CloudContainerAppName);

        var resourceGroupName = string.IsNullOrWhiteSpace(config.CloudResourceGroupName)
            ? $"rg-{containerAppName}"
            : config.CloudResourceGroupName.Trim();

        var registryName = string.IsNullOrWhiteSpace(config.CloudRegistryName)
            ? "ghcr.io"
            : config.CloudRegistryName.Trim();

        return new CloudNameTemplateInfo(containerAppName, resourceGroupName, registryName);
    }

    /// <summary>
    ///     Creates ordered custom environment variable template entries.
    /// </summary>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <returns>The custom environment list selection with its documented bounds and ordering.</returns>
    private static List<object> CreateCustomEnvironmentList(DeploymentConfigDto config)
    {
        return config.CustomEnvVars?
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select((kvp, index) => (object)new
            {
                index,
                key = kvp.Key.Trim(),
                value = kvp.Value,
                bicep_value_param = $"customEnv{index}Value",
                bicep_decoded_value = $"customEnv{index}DecodedValue",
                github_value_secret = CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(index,
                    kvp.Key.Trim()),
                container_secret_name = TemplateNameNormalizer.NormalizeContainerAppSecretName($"env-{index}-{kvp.Key}")
            })
            .ToList() ?? [];
    }

    /// <summary>
    ///     Main project path data exposed to templates.
    /// </summary>
    /// <param name="RelativePath">Validated path relative to the configured root.</param>
    /// <param name="Folder">Source or output folder used for deployment artifacts.</param>
    private readonly record struct MainProjectTemplateInfo(string RelativePath, string Folder);

    /// <summary>
    ///     Cloud resource name data exposed to templates.
    /// </summary>
    /// <param name="ContainerAppName">Verified Azure Container App name.</param>
    /// <param name="ResourceGroupName">Azure resource-group name used by this deployment.</param>
    /// <param name="RegistryName">Container registry name used by the deployment.</param>
    private readonly record struct CloudNameTemplateInfo(
        string ContainerAppName,
        string ResourceGroupName,
        string RegistryName);
}
