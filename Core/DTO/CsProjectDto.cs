namespace Core.DTO;

/// <summary>
///     Represents a C# project within a solution.
/// </summary>
public record CsProjectDto
{
    /// <summary>
    ///     The C# project name without the .csproj extension.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     The full path to the .csproj file.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    ///     Indicates whether the project is an ASP.NET Core web application.
    /// </summary>
    public bool IsWebProject { get; init; }
}