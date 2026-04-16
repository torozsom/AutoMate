namespace Core.DTO;

/// <summary>
///     Represents the metadata extracted from a .csproj file.
/// </summary>
public record ProjectMetadataDto
{
    /// <summary>
    ///     The target framework specified in the .csproj file (e.g., "net10.0").
    /// </summary>
    public string TargetFramework { get; init; } = string.Empty;

    /// <summary>
    ///     The .NET version targeted by the project (e.g., "10.0").
    /// </summary>
    public string DotNetVersion { get; init; } = string.Empty;

    /// <summary>
    ///     Indicates whether the project is a web application (e.g., ASP.NET Core) or not.
    /// </summary>
    public bool IsWebProject { get; init; }

    /// <summary>
    ///     A unique identifier associated with the User Secrets feature in .NET projects,
    ///     used to securely store sensitive information during development.
    /// </summary>
    public string? UserSecretsId { get; init; }

    /// <summary>
    ///     A list of project references (other .csproj files that this project depends on).
    /// </summary>
    public List<string> ProjectReferences { get; init; } = [];

    /// <summary>
    ///     A dictionary of package references and their versions (NuGet dependencies) used by the project.
    /// </summary>
    public Dictionary<string, string> PackageReferences { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     A dictionary of package references and their versions from referenced projects.
    /// </summary>
    public Dictionary<string, string> ReferencedProjectPackages { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     A flattened list of absolute paths to all .csproj files in the dependency graph.
    /// </summary>
    public HashSet<string> AllProjectPaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}