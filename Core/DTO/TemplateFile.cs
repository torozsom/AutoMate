namespace Core.DTO;

/// <summary>
///     Represents a generated template file ready to be written to disk or committed to a remote repository.
/// </summary>
public record TemplateFile
{
    /// <summary>
    ///     The relative path where the generated file should be written.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    ///     The generated file content.
    /// </summary>
    public string Content { get; init; } = string.Empty;
}