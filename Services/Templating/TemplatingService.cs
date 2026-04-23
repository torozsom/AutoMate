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
    private readonly string _templatesDirectory;

    public TemplateService(ILogger<TemplateService> logger)
    {
        _templatesDirectory = Path.Combine(AppContext.BaseDirectory, "Templating", "Templates");

        if (!Directory.Exists(_templatesDirectory))
        {
            logger.LogError("Templates directory not found at: {TemplatesDirectory}", _templatesDirectory);
            throw new DirectoryNotFoundException($"Templates directory not found at: {_templatesDirectory}");
        }
    }

    /// <summary>
    ///     Generates the content of a Dockerfile based on a predefined Scriban template and project details.
    /// </summary>
    /// <param name="projectName">
    ///     The name of the main project for which the Dockerfile is being generated.
    /// </param>
    /// <param name="dotNetVersion">
    ///     The target .NET version to be specified in the Dockerfile.
    /// </param>
    /// <param name="exposedPort">
    ///     The port number that should be exposed in the Dockerfile.
    /// </param>
    /// <param name="allProjectPaths">
    ///     A collection of all project file paths in the solution, used to build the Dockerfile context.
    /// </param>
    /// <param name="solutionRoot">
    ///     The root directory of the solution, used to compute relative paths for the projects.
    /// </param>
    /// <returns>
    ///     The generated Dockerfile content as a string.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    ///     Thrown if the Dockerfile template file is not found in the templates directory.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     Thrown if the main project file matching the provided project name cannot be found within the specified project
    ///     paths.
    /// </exception>
    public async Task<string> GenerateDockerfileAsync(string projectName, string dotNetVersion,
        int exposedPort, HashSet<string> allProjectPaths, string solutionRoot)
    {
        if (!File.Exists(Path.Combine(_templatesDirectory, "Dockerfile.scriban")))
            throw new FileNotFoundException("Dockerfile template not found.");

        // Load and parse the Dockerfile template
        var templatePath = Path.Combine(_templatesDirectory, "Dockerfile.scriban");
        var templateContent = await File.ReadAllTextAsync(templatePath);
        var template = Template.Parse(templateContent);

        // Find the main project file based on the provided project name
        var mainProjectPath =
            allProjectPaths.FirstOrDefault(p =>
                p.EndsWith($"{projectName}.csproj", StringComparison.OrdinalIgnoreCase));

        if (mainProjectPath == null)
            throw new InvalidOperationException(
                $"Main project file '{projectName}.csproj' not found in provided paths.");

        // Compute relative paths for all projects and the main project to be used in the template
        var mainProjectRelativePath = Path.GetRelativePath(solutionRoot, mainProjectPath).Replace('\\', '/');
        var mainDir = Path.GetDirectoryName(mainProjectRelativePath)?.Replace('\\', '/');
        var mainProjectFolder = string.IsNullOrEmpty(mainDir) ? "." : mainDir;

        // Build a list of project data with relative paths and folders for the template context
        var projectsData = allProjectPaths.Select(path =>
        {
            var relPath = Path.GetRelativePath(solutionRoot, path).Replace('\\', '/');
            var dir = Path.GetDirectoryName(relPath)?.Replace('\\', '/');

            return new
            {
                relative_path = relPath,
                folder = string.IsNullOrEmpty(dir) ? "." : dir
            };
        }).ToList();

        // Render the Dockerfile template with the provided data and return the generated content
        var result = await template.RenderAsync(new
        {
            project_name = projectName,
            dotnet_version = dotNetVersion,
            exposed_port = exposedPort,
            projects = projectsData,
            main_project_relative_path = mainProjectRelativePath,
            main_project_folder = mainProjectFolder
        });

        return result.Trim();
    }


    /// <summary>
    ///     Generates the content of a .dockerignore file using a predefined template.
    /// </summary>
    /// <returns>The generated .dockerignore file content as a string.</returns>
    /// <exception cref="FileNotFoundException">
    ///     Thrown if the .dockerignore template file is not found in the templates
    ///     directory.
    /// </exception>
    public async Task<string> GenerateDockerIgnoreAsync()
    {
        var templatePath = Path.Combine(_templatesDirectory, "dockerignore.scriban");

        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Template not found at: {templatePath}");

        var templateContent = await File.ReadAllTextAsync(templatePath);
        return templateContent.Trim();
    }


    /// <summary>
    ///     Saves the specified content to a file at the specified target directory with the given file name.
    /// </summary>
    /// <param name="targetDirectory">
    ///     The directory where the file should be saved. If the directory does not exist, it will be
    ///     created.
    /// </param>
    /// <param name="fileName">The name of the file to save the content to.</param>
    /// <param name="content">The content to be written to the file.</param>
    public async Task SaveFileAsync(string targetDirectory, string fileName, string content)
    {
        if (!Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        var filePath = Path.Combine(targetDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content);
    }


    /// <summary>
    ///     Generates the content of a docker-compose.yml file based on a predefined Scriban template and deployment configuration.
    /// </summary>
    /// <param name="config">The deployment configuration for the YAML file.</param>
    /// <returns>The content of the docker compose file.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<string> GenerateDockerComposeAsync(DeploymentConfigDto config)
    {
        // Check if the template file exists
        var templatePath = Path.Combine(_templatesDirectory, "docker-compose.scriban");
        if  (!File.Exists(templatePath))
            throw new FileNotFoundException($"Template not found at: {templatePath}");

        var templateContent = await File.ReadAllTextAsync(templatePath);

        // Parse the template and check for errors
        var template = Template.Parse(templateContent);
        if (template.HasErrors)
        {
            var errors = string.Join(", ", template.Messages.Select(m => m.Message));
            throw new InvalidOperationException($"Error parsing docker-compose template: {errors}");
        }

        // Build the model for the template rendering, including all necessary properties from the DeploymentConfigDto
        var model = new
        {
            project_name = config.ProjectName,
            environment_name = config.EnvironmentName,
            exposed_port = config.ExposedPort,
            requires_db = config.RequiresDb,
            db_type = config.DbType,
            db_name = config.DbName,
            db_user = config.DbUser,
            db_password = config.DbPassword,

            db_user_encoded = Uri.EscapeDataString(config.DbUser ?? ""),
            db_password_encoded = Uri.EscapeDataString(config.DbPassword ?? ""),

            // Convert the custom environment variables dictionary to a list of key-value pairs for easier use in the template
            custom_env_vars = config.CustomEnvVars.Select(kvp => new { key = kvp.Key, value = kvp.Value }).ToList()
        };

        // Render the template with the provided model and return the generated docker-compose.yml content
        var result = await template.RenderAsync(model);

        return result;
    }
}