namespace Core.DTO;


/// <summary>
///     Data Transfer Object (DTO) representing a local project on the file system.
/// </summary>
public class LocalProjectDto
{
    /// <summary>
    /// Gets or sets the name of the project.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full path to the project directory on the local file system.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the project is a valid .NET project.
    /// </summary>
    public bool IsDotNetProject { get; set; }

    /// <summary>
    /// Gets or sets the list of csprojects within the solution of the app.
    /// </summary>
    public IEnumerable<CsProjectDto> CsProjects { get; set; } = [];
}