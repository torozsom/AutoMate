using System.Xml.Linq;
using Core.DTO;

namespace Services.Scanner;

/// <summary>
///     Provides functionality to scan and analyze C# project files (.csproj) to extract
///     metadata such as target frameworks, dependencies, and project references.
/// </summary>
public class ProjectScannerService : IProjectScannerService
{
    /// <summary>
    ///     Scans a solution's project files (.csproj) to extract metadata such as target frameworks,
    ///     dependencies, and project references.
    /// </summary>
    /// <param name="filePath">The path to the project (.csproj) file to scan.</param>
    /// <returns>
    ///     A <see cref="ProjectMetadataDto" /> object containing metadata such as the target
    ///     framework, project references, package references, and other details.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    ///     Thrown when the specified project file does not exist at the given path.
    /// </exception>
    public async Task<ProjectMetadataDto> ScanProjectContentAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"The project file '{filePath}' does not exist.");

        // Scan the main project file first
        var mainContent = await File.ReadAllTextAsync(filePath);
        var mainMetadata = await ScanCsprojFileContentAsync(mainContent);

        // Initialize data structures to track referenced projects and their packages
        var referencedProjectPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectsToProcess = new Queue<(string Path, ProjectMetadataDto Metadata)>();

        var fullFilePath = Path.GetFullPath(filePath);
        allProjectPaths.Add(fullFilePath);
        visitedPaths.Add(fullFilePath);

        // Start with the main project
        projectsToProcess.Enqueue((fullFilePath, mainMetadata));

        // Process the queue of projects to scan their references recursively
        while (projectsToProcess.Count > 0)
        {
            var (currentPath, currentMetadata) = projectsToProcess.Dequeue();
            var currentDir = Path.GetDirectoryName(currentPath) ?? string.Empty;

            foreach (var relativeRef in currentMetadata.ProjectReferences)
            {
                var absoluteRefPath = Path.GetFullPath(Path.Combine(currentDir, relativeRef));
                if (visitedPaths.Contains(absoluteRefPath) || !File.Exists(absoluteRefPath)) continue;

                visitedPaths.Add(absoluteRefPath);
                allProjectPaths.Add(absoluteRefPath);

                var refContent = await File.ReadAllTextAsync(absoluteRefPath);
                var refMetadata = await ScanCsprojFileContentAsync(refContent);

                foreach (var pkg in refMetadata.PackageReferences)
                    referencedProjectPackages.TryAdd(pkg.Key, pkg.Value);

                projectsToProcess.Enqueue((absoluteRefPath, refMetadata));
            }
        }

        // Combine the package references from all projects
        return mainMetadata with
        {
            ReferencedProjectPackages = referencedProjectPackages,
            AllProjectPaths = allProjectPaths
        };
    }


    /// <summary>
    ///     Parses the provided project XML content and extracts metadata such as target framework,
    ///     dependencies, project references, and other configuration details.
    /// </summary>
    /// <param name="xmlContent">The XML content of the project file to scan.</param>
    /// <returns>
    ///     A <see cref="ProjectMetadataDto" /> object containing information about the project's
    ///     target framework, .NET version, package references, project references, and other metadata.
    /// </returns>
    /// <exception cref="XmlException">
    ///     Thrown when the provided XML content is not well-formed or cannot be parsed.
    /// </exception>
    public Task<ProjectMetadataDto> ScanCsprojFileContentAsync(string xmlContent)
    {
        var document = XDocument.Parse(xmlContent);
        var targetFramework = document.Descendants("TargetFramework").FirstOrDefault()?.Value;

        if (string.IsNullOrEmpty(targetFramework))
            targetFramework = "net10.0";

        var dotNetVersion = targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? targetFramework[3..]
            : "10.0";

        var sdkAttribute = document.Root?.Attribute("Sdk")?.Value;
        var isWebProject = sdkAttribute != null
                           && sdkAttribute.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);

        var userSecretsId = document.Descendants("UserSecretsId").FirstOrDefault()?.Value;

        var packageReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in document.Descendants("PackageReference"))
        {
            var include = pr.Attribute("Include")?.Value;
            var version = pr.Attribute("Version")?.Value;
            if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
                packageReferences[include] = version;
        }

        var projectReferences = new List<string>();
        foreach (var pr in document.Descendants("ProjectReference"))
        {
            var include = pr.Attribute("Include")?.Value;

            if (string.IsNullOrEmpty(include)) continue;
            var normalizedPath = include.Replace('\\', '/');
            projectReferences.Add(normalizedPath);
        }

        var metadata = new ProjectMetadataDto
        {
            TargetFramework = targetFramework,
            DotNetVersion = dotNetVersion,
            IsWebProject = isWebProject,
            UserSecretsId = userSecretsId,
            PackageReferences = packageReferences,
            ProjectReferences = projectReferences
        };

        return Task.FromResult(metadata);
    }
}