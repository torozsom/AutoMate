using Core.DTO;
using Core.Entities;

namespace Services.Scanner;

/// <summary>
///     Service responsible for analyzing .csproj files to extract metadata
///     such as the target .NET version, project type, and dependencies.
/// </summary>
public interface IProjectScannerService
{
    /// Scans the provided XML content to extract solution-wide metadata.
    Task<ProjectMetadataDto> ScanProjectContentAsync(string xmlContent);

    /// Analyzes the dependencies of the given project to determine deployment configurations.
    Task<DeploymentConfigDto> AnalyzeDependenciesAsync(Project project, CsProject csProject);

    /// Extracts environment variables from configuration files in the project directory.
    Task<Dictionary<string, string>> ExtractEnvironmentVariablesAsync(string projectPath);
}