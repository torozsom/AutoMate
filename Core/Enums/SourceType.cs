namespace Core.Enums;

/// <summary>
///     Defines the source types for projects, indicating whether
///     a project is sourced from a local repository or a remote repository.
/// </summary>
public enum SourceType
{
    /// <summary>
    ///     The project source code is located in a local file system directory.
    /// </summary>
    Local,

    /// <summary>
    ///     The project source code is hosted in a remote repository (e.g., GitHub).
    /// </summary>
    Remote
}