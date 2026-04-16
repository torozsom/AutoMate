namespace Services.Templating;

/// <summary>
///     Service responsible for dynamically generating infrastructure-as-code files
///     (like Dockerfile, docker-compose.yml) based on project configurations.
/// </summary>
public interface ITemplateService
{
    /// Generates a Dockerfile based on the provided project name, .NET version, exposed port, and project paths.
    Task<string> GenerateDockerfileAsync(string projectName, string dotNetVersion,
        int exposedPort, HashSet<string> allProjectPaths, string solutionRoot);

    /// Generates the content of a .dockerignore file using a predefined template.
    Task<string> GenerateDockerIgnoreAsync();

    /// Saves a file to the specified directory with the specified name and content.
    Task SaveFileAsync(string targetDirectory, string fileName, string content);
}