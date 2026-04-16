using Scriban;

namespace Services.Templating;

/// <summary>
///     Provides functionality for generating templated files such as Dockerfile and .dockerignore
///     based on predefined Scriban templates and for saving generated files to a specified directory.
/// </summary>
public class TemplateService : ITemplateService
{
    private readonly string _templatesDirectory = Path.Combine(AppContext.BaseDirectory, "Templating", "Templates");

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
        var templatePath = Path.Combine(_templatesDirectory, "Dockerfile.scriban");
        var templateContent = await File.ReadAllTextAsync(templatePath);
        var template = Template.Parse(templateContent);

        var mainProjectPath =
            allProjectPaths.First(p => p.EndsWith($"{projectName}.csproj", StringComparison.OrdinalIgnoreCase));
        var mainProjectRelativePath = Path.GetRelativePath(solutionRoot, mainProjectPath).Replace('\\', '/');
        var mainProjectFolder = Path.GetDirectoryName(mainProjectRelativePath)?.Replace('\\', '/') ?? string.Empty;

        var projectsData = allProjectPaths.Select(path => new
        {
            relative_path = Path.GetRelativePath(solutionRoot, path).Replace('\\', '/'),
            folder = Path.GetDirectoryName(Path.GetRelativePath(solutionRoot, path))?.Replace('\\', '/') ?? string.Empty
        }).ToList();

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
}