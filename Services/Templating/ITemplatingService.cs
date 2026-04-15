namespace Services.Templating;

/// <summary>
///     Service responsible for dynamically generating infrastructure-as-code files
///     (like Dockerfile, docker-compose.yml) based on project configurations.
/// </summary>
public interface ITemplateService
{
    /// <summary>
    ///     Generates the content of a Dockerfile for a .NET project.
    /// </summary>
    /// <param name="projectName">The name of the C# project (e.g., "MyWebApi").</param>
    /// <param name="dotNetVersion">The target .NET version (e.g., "10.0").</param>
    /// <param name="exposedPort">The port the application listens on (e.g., 8080).</param>
    /// <returns>The generated Dockerfile content as a string.</returns>
    Task<string> GenerateDockerfileAsync(string projectName, string dotNetVersion, int exposedPort);

    /// <summary>
    ///     Generates a standard .dockerignore file to prevent build artifacts from bloating the image.
    /// </summary>
    /// <returns>The generated .dockerignore content as a string.</returns>
    Task<string> GenerateDockerIgnoreAsync();

    /// <summary>
    ///     Saves the generated content to the specified file path.
    /// </summary>
    Task SaveFileAsync(string targetDirectory, string fileName, string content);
}