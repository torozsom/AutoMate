using Core.DTO;

namespace Services.Scanner;

/// <summary>
///     Walks project references from a root .csproj file and combines referenced project metadata.
/// </summary>
internal static class ProjectDependencyGraphScanner
{
    /// <summary>
    ///     Scans the root project and recursively scans referenced projects once.
    /// </summary>
    public static async Task<ProjectMetadataDto> ScanAsync(string filePath, CancellationToken cancellationToken)
    {
        var mainMetadata = await CsprojMetadataReader.ReadAsync(filePath, cancellationToken);
        var referencedProjectPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectsToProcess = new Queue<ProjectScanTarget>();

        var fullFilePath = Path.GetFullPath(filePath);
        allProjectPaths.Add(fullFilePath);
        visitedPaths.Add(fullFilePath);
        projectsToProcess.Enqueue(new ProjectScanTarget(fullFilePath, mainMetadata));

        while (projectsToProcess.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = projectsToProcess.Dequeue();
            var currentDir = Path.GetDirectoryName(current.Path) ?? string.Empty;

            foreach (var relativeRef in current.Metadata.ProjectReferences)
            {
                var absoluteRefPath = Path.GetFullPath(Path.Combine(currentDir, relativeRef));
                if (visitedPaths.Contains(absoluteRefPath) || !File.Exists(absoluteRefPath))
                    continue;

                visitedPaths.Add(absoluteRefPath);
                allProjectPaths.Add(absoluteRefPath);

                var refMetadata = await CsprojMetadataReader.ReadAsync(absoluteRefPath, cancellationToken);
                foreach (var package in refMetadata.PackageReferences)
                    referencedProjectPackages.TryAdd(package.Key, package.Value);

                projectsToProcess.Enqueue(new ProjectScanTarget(absoluteRefPath, refMetadata));
            }
        }

        return mainMetadata with
        {
            ReferencedProjectPackages = referencedProjectPackages,
            AllProjectPaths = allProjectPaths
        };
    }

    /// <summary>
    ///     Queue item for dependency graph traversal.
    /// </summary>
    private readonly record struct ProjectScanTarget(string Path, ProjectMetadataDto Metadata);
}