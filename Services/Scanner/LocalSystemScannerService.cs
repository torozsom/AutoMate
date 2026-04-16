using System.Xml.Linq;
using Core.DTO;

namespace Services.Scanner;

/// <summary>
///     The LocalScannerService class is responsible for scanning the local file system
///     to identify Git repositories and determine if they are .NET projects. It implements
///     the ILocalScannerService interface, providing a method to scan a specified root directory
///     and its subdirectories for Git repositories.
/// </summary>
public class LocalSystemScannerService : ILocalSystemScannerService
{
    /// <summary>
    ///     Scans the specified root directory and its subdirectories for Git repositories.
    ///     For each Git repository found, it checks for the presence of .NET project files
    ///     (.sln or .csproj) to determine if it's a .NET project.
    /// </summary>
    /// <param name="rootPath">The root path where we start scanning.</param>
    /// <returns>A list of LocalProjectDto objects representing the found projects.</returns>
    public Task<List<LocalProjectDto>> ScanForProjectsAsync(string rootPath)
    {
        var result = new List<LocalProjectDto>();

        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return Task.FromResult(result);

        ScanDirectory(rootPath, result);

        return Task.FromResult(result);
    }


    /// <summary>
    ///     Recursively scans the directory for Git repositories and identifies .NET projects.
    ///     Skips common build and dependency folders, as well as hidden directories.
    /// </summary>
    /// <param name="currentPath">The current directory path being scanned.</param>
    /// <param name="result">The list to accumulate found local projects.</param>
    private void ScanDirectory(string currentPath, List<LocalProjectDto> result)
    {
        try
        {
            var dirInfo = new DirectoryInfo(currentPath);

            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return;

            var dirName = dirInfo.Name;

            if (dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                dirName.StartsWith('.'))
                return;

            var directories = Directory.GetDirectories(currentPath);

            var isGitRepo =
                directories.Any(d => Path.GetFileName(d).Equals(".git", StringComparison.OrdinalIgnoreCase));

            if (isGitRepo)
            {
                var csprojFiles = FindCsprojFilesSafe(currentPath);
                var files = Directory.GetFiles(currentPath);

                var isDotNet = files.Any(f =>
                    f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                    csprojFiles.Count > 0);

                var subProjects = new List<CsProjectDto>();

                foreach (var csproj in csprojFiles)
                {
                    var isWeb = false;
                    try
                    {
                        var doc = XDocument.Load(csproj);
                        var sdkAttribute = doc.Root?.Attribute("Sdk")?.Value;
                        if (sdkAttribute != null && sdkAttribute.Contains("Microsoft.NET.Sdk.Web"))
                            isWeb = true;
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"Error parsing .csproj file: {csproj}. Exception: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Unexpected error parsing .csproj file: {csproj}. Exception: {ex.Message}");
                    }

                    subProjects.Add(new CsProjectDto
                    {
                        Name = Path.GetFileNameWithoutExtension(csproj),
                        Path = csproj,
                        IsWebProject = isWeb
                    });
                }

                result.Add(new LocalProjectDto
                {
                    Name = dirName,
                    Path = currentPath,
                    IsDotNetProject = isDotNet,
                    CsProjects = subProjects
                });

                return;
            }

            foreach (var dir in directories)
                if (!Path.GetFileName(dir).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    ScanDirectory(dir, result);
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine("Access denied to directory: " + currentPath);
        }
        catch (Exception)
        {
            Console.WriteLine("Error scanning directory: " + currentPath);
        }
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
                var dirName = new DirectoryInfo(currentPath).Name;

                if (currentPath != rootDir &&
                    (dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                     dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                     dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                     dirName.StartsWith('.'))) continue;

                result.AddRange(Directory.GetFiles(currentPath, "*.csproj"));
                foreach (var subDir in Directory.GetDirectories(currentPath))
                    queue.Enqueue(subDir);
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Access denied to directory: " + currentPath);
            }
            catch (Exception)
            {
                Console.WriteLine("Error scanning directory: " + currentPath);
            }
        }

        return result;
    }


    /// <summary>
    ///     Finds the root directory of the solution for a given project file path by traversing
    ///     upward through the directory hierarchy. A solution root is identified by the presence
    ///     of a .git folder or solution files (*.sln, *.slnx) in the directory.
    /// </summary>
    /// <param name="projectFilePath">The full path to the project file for which the solution root is to be located.</param>
    /// <returns>The full path to the solution root directory. If no solution root is found, it returns the directory of the provided project file.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the specified project file path does not exist or is invalid.</exception>
    public Task<string> FindSolutionRootAsync(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
            throw new FileNotFoundException($"The project file '{projectFilePath}' does not exist.");

        var currentDir = new DirectoryInfo(Path.GetDirectoryName(projectFilePath) ?? string.Empty);
        while (currentDir != null)
        {
            if (Directory.Exists(Path.Combine(currentDir.FullName, ".git"))
                || currentDir.GetFiles("*.sln").Length > 0
                || currentDir.GetFiles("*.slnx").Length > 0)
                return Task.FromResult(currentDir.FullName);

            currentDir = currentDir.Parent;
        }

        var fallbackDir = Path.GetDirectoryName(projectFilePath) ?? string.Empty;
        return Task.FromResult(fallbackDir);
    }
}