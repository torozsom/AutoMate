using Core.DTO;

namespace Services.Scanner;

/// <summary>
///     Service interface for scanning the local file system to identify Git projects.
/// </summary>
public interface ILocalSystemScannerService
{
    /// Scans the specified root directory and its subdirectories for Git repositories.
    Task<List<LocalProjectDto>> ScanForProjectsAsync(string rootPath);
}