namespace Core.DTO;

/// <summary>
///     Represents a C# project within a solution.
/// </summary>
public record CsProjectDto
{
    /// <summary>
    ///     Gets or sets the name of the csproject.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the path to the csproject.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether the csproject is a web application.
    /// </summary>
    public bool IsWebProject { get; set; }
}