using Core.DTO;

namespace Services.Scanner;


/// <summary>
///    Service interface for scanning the local file system to identify Git projects.
/// </summary>
public interface ILocalScannerService
{
    /// <summary>
    ///     Scans a root directory and returns a list of local projects found within it.
    /// </summary>
    /// <param name="rootPath">The root directory path to start the scan from.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="LocalProjectDto"/> representing the found projects.</returns>
    Task<List<LocalProjectDto>> ScanForProjectsAsync(string rootPath);
}