using Core.DTO;
using Core.Entities;

namespace Services.Scanner;

/// <summary>
///     Service responsible for analyzing .csproj files to extract metadata
///     such as the target .NET version, project type, and dependencies.
/// </summary>
public interface IProjectScannerService
{
    /// <summary>
    ///     Scans the provided project file path to extract solution-wide metadata.
    /// </summary>
    /// <param name="filePath">The full path to the .csproj file to scan.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>Project metadata containing references and target framework.</returns>
    Task<ProjectMetadataDto> ScanProjectContentAsync(string filePath, CancellationToken cancellationToken = default);


    /// <summary>
    ///     Analyzes the dependencies of the given project to determine deployment configurations.
    /// </summary>
    /// <param name="project">The parent project entity.</param>
    /// <param name="csProject">The specific C# project being analyzed.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A deployment configuration including identified database dependencies.</returns>
    Task<DeploymentConfigDto> AnalyzeDependenciesAsync(Project project, CsProject csProject,
        CancellationToken cancellationToken = default);


    /// <summary>
    ///     Extracts environment variables from configuration files in the project directory.
    /// </summary>
    /// <param name="projectPath">The root path of the project containing configuration files.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A dictionary of extracted environment variables.</returns>
    Task<Dictionary<string, string>> ExtractEnvironmentVariablesAsync(string projectPath,
        CancellationToken cancellationToken = default);
}