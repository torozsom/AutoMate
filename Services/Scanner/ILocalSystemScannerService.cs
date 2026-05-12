using Core.DTO;

namespace Services.Scanner;

/// <summary>
///     Service interface for scanning the local file system to identify Git projects.
/// </summary>
public interface ILocalSystemScannerService
{
    /// <summary>
    ///     Scans the specified root directory and its subdirectories for Git repositories.
    /// </summary>
    /// <param name="rootPath">The root directory path where scanning begins.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A list of LocalProjectDto objects representing the found projects.</returns>
    Task<List<LocalProjectDto>> ScanForProjectsAsync(string rootPath, CancellationToken cancellationToken = default);


    /// <summary>
    ///     Finds the root directory of a solution by traversing up the directory tree
    ///     looking for .git, .sln, or .slnx files.
    /// </summary>
    /// <param name="projectFilePath">The full path to the project file.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The full path to the solution root directory, or the project directory as a fallback.</returns>
    Task<string> FindSolutionRootAsync(string projectFilePath, CancellationToken cancellationToken = default);
}