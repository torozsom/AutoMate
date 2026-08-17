using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Scanner;

/// <summary>
///     Scans local directories for Git repositories that contain .NET solution or project files.
/// </summary>
public sealed class LocalSystemScannerService(ILogger<LocalSystemScannerService> logger) : ILocalSystemScannerService
{
    /// <inheritdoc />
    public async Task<List<LocalProjectDto>> ScanForProjectsAsync(string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            logger.LogWarning(
                "[LocalSystemScannerService] Invalid or non-existent root path provided for scanning: {RootPath}",
                rootPath);
            return [];
        }

        logger.LogInformation("[LocalSystemScannerService] Starting system scan for .NET projects in: {RootPath}",
            rootPath);

        var result = new List<LocalProjectDto>();
        await ScanDirectorySafeAsync(Path.GetFullPath(rootPath), result, cancellationToken);

        logger.LogInformation("[LocalSystemScannerService] Scan completed. Found {Count} local projects.",
            result.Count);
        return result;
    }


    /// <inheritdoc />
    public Task<string> FindSolutionRootAsync(string projectFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
        {
            logger.LogError("[LocalSystemScannerService] Project file not found: {ProjectFilePath}", projectFilePath);
            throw new FileNotFoundException($"The project file '{projectFilePath}' does not exist.");
        }

        var currentDir = new DirectoryInfo(Path.GetDirectoryName(projectFilePath) ?? string.Empty);
        while (currentDir != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (LocalScannerDirectoryRules.IsSolutionRoot(currentDir))
            {
                logger.LogDebug("[LocalSystemScannerService] Solution root found at: {SolutionRoot}",
                    currentDir.FullName);
                return Task.FromResult(currentDir.FullName);
            }

            currentDir = currentDir.Parent;
        }

        // If no solution root is found, log a warning and return the directory of the project file as a fallback
        var fallbackDir = Path.GetDirectoryName(projectFilePath) ?? string.Empty;
        logger.LogWarning(
            "[LocalSystemScannerService] Could not find a distinct solution root. Falling back to project directory: {FallbackDir}",
            fallbackDir);

        return Task.FromResult(fallbackDir);
    }


    /// <summary>
    ///     Recursively scans the directory for Git repositories and identifies .NET projects.
    ///     Skips common build and dependency folders, as well as hidden directories.
    /// </summary>
    /// <param name="currentPath">The current directory path being scanned.</param>
    /// <param name="result">The list to accumulate found local projects.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    private async Task ScanDirectorySafeAsync(string currentPath, List<LocalProjectDto> result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var dirInfo = new DirectoryInfo(currentPath);

            if (LocalScannerDirectoryRules.IsExcludedDirectory(dirInfo))
                return;

            var directories = Directory.GetDirectories(currentPath);
            if (LocalScannerDirectoryRules.ContainsGitDirectory(directories))
            {
                await ProcessGitRepositoryAsync(currentPath, dirInfo.Name, result, cancellationToken);
                return;
            }

            foreach (var dir in directories)
                await ScanDirectorySafeAsync(dir, result, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(
                "[LocalSystemScannerService] Scan cancelled for directory: {CurrentPath}, Exception: {Exception}",
                currentPath, ex.Message);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug("[LocalSystemScannerService] Access denied to directory, skipping: {CurrentPath}." +
                            "Exception: {Exception}", currentPath, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LocalSystemScannerService] Unexpected error while scanning directory: {CurrentPath}",
                currentPath);
        }
    }


    /// <summary>
    ///     Processes a Git repository directory to determine if it contains .NET project files (.sln or .csproj).
    ///     If .NET project files are found, it creates a LocalProjectDto object and adds it to the result list.
    /// </summary>
    /// <param name="repoPath">The root path to the repository to be processed.</param>
    /// <param name="dirName">The directory name of the repository.</param>
    /// <param name="result">The result list to be appended.</param>
    private async Task ProcessGitRepositoryAsync(string repoPath, string dirName, List<LocalProjectDto> result,
        CancellationToken cancellationToken)
    {
        try
        {
            var csprojFiles = FindCsprojFilesSafe(repoPath, cancellationToken);
            var isDotNet = LocalScannerDirectoryRules.HasSolutionFile(repoPath) || csprojFiles.Count > 0;

            var subProjects = new List<CsProjectDto>();
            foreach (var csprojFile in csprojFiles)
                subProjects.Add(await LocalCsProjectParser.ParseAsync(csprojFile, logger, cancellationToken));

            result.Add(new LocalProjectDto
            {
                Name = dirName,
                Path = repoPath,
                IsDotNetProject = isDotNet,
                CsProjects = subProjects
            });
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(
                "[LocalSystemScannerService] Scan cancelled for repository: {RepoPath}, Exception: {Exception}",
                repoPath, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LocalSystemScannerService] Error processing git repository at {RepoPath}", repoPath);
        }
    }


    /// <summary>
    ///     Safely searches for .csproj files within the specified root directory
    ///     and all its subdirectories, excluding certain directories like "bin",
    ///     "obj", "node_modules", and hidden directories.
    /// </summary>
    /// <param name="rootDir">The root directory to start searching for .csproj files.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A list of file paths to the .csproj files found.</returns>
    private List<string> FindCsprojFilesSafe(string rootDir, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(rootDir);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentPath = queue.Dequeue();
            try
            {
                if (currentPath != rootDir &&
                    LocalScannerDirectoryRules.IsExcludedDirectory(new DirectoryInfo(currentPath)))
                    continue;

                result.AddRange(Directory.GetFiles(currentPath, "*.csproj"));

                foreach (var subDir in Directory.GetDirectories(currentPath))
                    queue.Enqueue(subDir);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogDebug(
                    "[LocalSystemScannerService] Access denied while searching for .csproj files: {CurrentPath}. " +
                    "Exception: {Exception}", currentPath, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[LocalSystemScannerService] Error scanning for .csproj in directory: {CurrentPath}", currentPath);
            }
        }

        return result;
    }
}