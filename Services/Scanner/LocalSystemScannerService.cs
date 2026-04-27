using System.Xml.Linq;
using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Scanner;

/// <summary>
///     The LocalScannerService class is responsible for scanning the local file system
///     to identify Git repositories and determine if they are .NET projects. It implements
///     the ILocalScannerService interface, providing a method to scan a specified root directory
///     and its subdirectories for Git repositories.
/// </summary>
public class LocalSystemScannerService(ILogger<LocalSystemScannerService> logger) : ILocalSystemScannerService
{
    /// A set of directory names that should be ignored during the scanning process.
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "testresults", ".vs", ".idea"
    };

    /// <summary>
    ///     Scans the specified root directory and its subdirectories for Git repositories.
    ///     For each Git repository found, it checks for the presence of .NET project files
    ///     (.sln or .csproj) to determine if it's a .NET project.
    /// </summary>
    /// <param name="rootPath">The root path where we start scanning.</param>
    /// <returns>A list of LocalProjectDto objects representing the found projects.</returns>
    public async Task<List<LocalProjectDto>> ScanForProjectsAsync(string rootPath)
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

        // Perform the scanning operation in a background task to avoid blocking the main thread
        return await Task.Run(() =>
        {
            var result = new List<LocalProjectDto>();
            ScanDirectorySafe(rootPath, result);
            logger.LogInformation("[LocalSystemScannerService] Scan completed. Found {Count} local projects.",
                result.Count);
            return result;
        });
    }


    /// <summary>
    ///     Finds the solution root directory for a given project file path. It traverses up the directory
    ///     hierarchy from the project file's location, looking for indicators of a solution root such as a .git folder
    ///     or .sln/.slnx files.
    /// </summary>
    /// <param name="projectFilePath">The full path to the project file for which the solution root is to be located.</param>
    /// <returns>
    ///     The full path to the solution root directory. If no solution root is found, it returns the directory of the
    ///     provided project file.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown if the specified project file path does not exist or is invalid.</exception>
    public Task<string> FindSolutionRootAsync(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
        {
            logger.LogError("[LocalSystemScannerService] Project file not found: {ProjectFilePath}", projectFilePath);
            throw new FileNotFoundException($"The project file '{projectFilePath}' does not exist.");
        }

        // Start from the directory of the project file
        var currentDir = new DirectoryInfo(Path.GetDirectoryName(projectFilePath) ?? string.Empty);

        // Traverse up the directory hierarchy to find a solution root
        while (currentDir != null)
        {
            if (Directory.Exists(Path.Combine(currentDir.FullName, ".git")) ||
                currentDir.EnumerateFiles("*.sln").Any() ||
                currentDir.EnumerateFiles("*.slnx").Any())
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
    private void ScanDirectorySafe(string currentPath, List<LocalProjectDto> result)
    {
        try
        {
            var dirInfo = new DirectoryInfo(currentPath);

            if (IsExcludedDirectory(dirInfo))
                return;

            // Check if the current directory is a Git repository by looking for a .git folder
            var directories = Directory.GetDirectories(currentPath);
            var isGitRepo =
                directories.Any(d => Path.GetFileName(d).Equals(".git", StringComparison.OrdinalIgnoreCase));

            // If it's a Git repository, process it to determine if it's a .NET project and add it to the result list
            if (isGitRepo)
            {
                ProcessGitRepository(currentPath, dirInfo.Name, result);
                return;
            }

            // If it's not a Git repository, continue scanning subdirectories
            foreach (var dir in directories)
                ScanDirectorySafe(dir, result);
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogDebug("[LocalSystemScannerService] Access denied to directory, skipping: {CurrentPath}",
                currentPath);
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
    private void ProcessGitRepository(string repoPath, string dirName, List<LocalProjectDto> result)
    {
        try
        {
            // Safely find all .csproj files in the repository, excluding common build and dependency folders
            var csprojFiles = FindCsprojFilesSafe(repoPath);

            // Determine if this repository is a .NET project by checking for the presence of .sln files or .csproj files
            var isDotNet = Directory.EnumerateFiles(repoPath).Any(f =>
                               f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                           || csprojFiles.Count > 0;

            // Parse each .csproj file to determine if it's a web project and create CsProjectDto objects for them
            var subProjects = csprojFiles.Select(ParseCsProject).ToList();

            // If it's a .NET project, add it to the result list with its name, path, and sub-projects
            result.Add(new LocalProjectDto
            {
                Name = dirName,
                Path = repoPath,
                IsDotNetProject = isDotNet,
                CsProjects = subProjects
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LocalSystemScannerService] Error processing git repository at {RepoPath}", repoPath);
        }
    }


    /// <summary>
    ///     Parses a .csproj file to determine if it is a web project by checking
    ///     for the presence of the "Microsoft.NET.Sdk.Web" SDK attribute.
    /// </summary>
    /// <param name="csprojPath">The path to .csproj file.</param>
    /// <returns></returns>
    private CsProjectDto ParseCsProject(string csprojPath)
    {
        var isWeb = false;
        try
        {
            // Load the .csproj file as an XML document and check for the "Sdk" attribute in the root element
            var doc = XDocument.Load(csprojPath);
            var sdkAttribute = doc.Root?.Attribute("Sdk")?.Value;
            isWeb = sdkAttribute != null &&
                    sdkAttribute.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalSystemScannerService] Failed to parse .csproj file " +
                                  "to determine project type: {CsProjPath}", csprojPath);
        }

        // Create and return a CsProjectDto with the project name, path, and whether it's a web project
        return new CsProjectDto
        {
            Name = Path.GetFileNameWithoutExtension(csprojPath),
            Path = csprojPath,
            IsWebProject = isWeb
        };
    }


    /// <summary>
    ///     Safely searches for .csproj files within the specified root directory
    ///     and all its subdirectories, excluding certain directories like "bin",
    ///     "obj", "node_modules", and hidden directories.
    /// </summary>
    /// <param name="rootDir">The root directory to start searching for .csproj files.</param>
    /// <returns>A list of file paths to the .csproj files found.</returns>
    private List<string> FindCsprojFilesSafe(string rootDir)
    {
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(rootDir);

        while (queue.Count > 0)
        {
            var currentPath = queue.Dequeue();
            try
            {
                if (currentPath != rootDir && IsExcludedDirectory(new DirectoryInfo(currentPath)))
                    continue;

                result.AddRange(Directory.GetFiles(currentPath, "*.csproj"));

                foreach (var subDir in Directory.GetDirectories(currentPath))
                    queue.Enqueue(subDir);
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogDebug(
                    "[LocalSystemScannerService] Access denied while searching for .csproj files: {CurrentPath}",
                    currentPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[LocalSystemScannerService] Error scanning for .csproj in directory: {CurrentPath}", currentPath);
            }
        }

        return result;
    }


    /// <summary>
    ///     Determines if a directory should be excluded from scanning based on its attributes and name.
    ///     It excludes directories that are reparse points (like symbolic links), hidden directories
    ///     that start with a dot (except for ".git"), and directories that are commonly used for build outputs.
    /// </summary>
    /// <param name="dirInfo">
    ///     The DirectoryInfo object representing the directory to be evaluated for exclusion.
    /// </param>
    /// <returns>
    ///     True if the directory should be excluded from scanning, otherwise, false.
    /// </returns>
    private static bool IsExcludedDirectory(DirectoryInfo dirInfo)
    {
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return true;

        var name = dirInfo.Name;
        if (name.StartsWith('.') && !name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            return true;

        return IgnoredDirectories.Contains(name);
    }
}