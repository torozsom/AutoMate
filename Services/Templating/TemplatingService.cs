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
    ///     Generates the content of a Dockerfile using a predefined Scriban template.
    /// </summary>
    /// <param name="projectName">The name of the project to use in the template.</param>
    /// <param name="dotNetVersion">The version of .NET to include in the Dockerfile.</param>
    /// <param name="exposedPort">The port to expose in the Dockerfile.</param>
    /// <returns>The generated Dockerfile content as a string.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the Dockerfile template file is not found in the templates directory.</exception>
    /// <exception cref="InvalidOperationException">Thrown if there are errors while parsing the Dockerfile template.</exception>
    public async Task<string> GenerateDockerfileAsync(string projectName, string dotNetVersion, int exposedPort)
    {
        var templatePath = Path.Combine(_templatesDirectory, "Dockerfile.scriban");

        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Template not found at: {templatePath}");

        var templateContent = await File.ReadAllTextAsync(templatePath);
        var template = Template.Parse(templateContent);

        if (template.HasErrors)
        {
            var errors = string.Join("\n", template.Messages.Select(x => x.Message));
            throw new InvalidOperationException($"Template parse error: {errors}");
        }

        var result = await template.RenderAsync(new
        {
            project_name = projectName,
            dotnet_version = dotNetVersion,
            exposed_port = exposedPort
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