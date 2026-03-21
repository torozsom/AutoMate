using Core.DTO;


namespace Services.Scanner;

/// <summary>
///     The LocalScannerService class is responsible for scanning the local file system
///     to identify Git repositories and determine if they are .NET projects. It implements
///     the ILocalScannerService interface, providing a method to scan a specified root directory
///     and its subdirectories for Git repositories.
/// </summary>
public class LocalScannerService : ILocalScannerService
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
                var files = Directory.GetFiles(currentPath);

                var isDotNet = files.Any(f =>
                    f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

                result.Add(new LocalProjectDto
                {
                    Name = dirName,
                    Path = currentPath,
                    IsDotNetProject = isDotNet
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
}