namespace Core.DTO;


/// <summary>
///     Data Transfer Object (DTO) representing a local project on the file system.
/// </summary>
public class LocalProjectDto
{
    /// The name of the project.
    public string Name { get; set; } = string.Empty;

    /// The path to the project on the local file system.
    public string Path { get; set; } = string.Empty;

    /// Indicates whether the project is a .NET project.
    public bool IsDotNetProject { get; set; }

}