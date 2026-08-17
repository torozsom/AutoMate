namespace Core.DTO;

/// <summary>
///     Data Transfer Object (DTO) representing a local project on the file system.
/// </summary>
public record LocalProjectDto
{
    /// <summary>
    ///     The local repository or solution folder name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     The full path to the local repository or solution folder.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    ///     Indicates whether the scanned folder contains one or more .NET projects.
    /// </summary>
    public bool IsDotNetProject { get; init; }

    /// <summary>
    ///     The discovered C# projects inside the scanned repository or solution folder.
    /// </summary>
    public IEnumerable<CsProjectDto> CsProjects { get; init; } = [];
}