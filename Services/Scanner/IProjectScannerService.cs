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

    /// Scans a .csproj file located at the specified file path to extract project metadata.
    Task<ProjectMetadataDto> ScanCsprojFileContentAsync(string filePath);

    Task<DeploymentConfigDto> AnalyzeDependenciesAsync(Project project, CsProject csProject);
}