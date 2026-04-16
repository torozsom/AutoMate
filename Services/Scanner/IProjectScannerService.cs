using Core.DTO;

namespace Services.Scanner;

/// <summary>
///     Service responsible for analyzing .csproj files to extract metadata
///     such as the target .NET version, project type, and dependencies.
/// </summary>
public interface IProjectScannerService
{
    ///
    Task<ProjectMetadataDto> ScanProjectContentAsync(string xmlContent);

    /// Scans a .csproj file located at the specified file path to extract project metadata.
    Task<ProjectMetadataDto> ScanLocalProjectAsync(string filePath);
}