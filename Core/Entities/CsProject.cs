namespace Core.Entities;

/// <summary>
///     Represents a C# project within a solution.
/// </summary>
public class CsProject
{
    /// <summary>
    ///     Gets or sets the unique identifier for the C# project.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     Gets or sets the unique identifier of the project associated with this C# project.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    ///     Gets or sets the reference to the project associated with this C# project.
    /// </summary>
    public Project Project { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the name of the C# project.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the path to the C# project.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether the C# project is a web application.
    /// </summary>
    public bool IsWebProject { get; set; }

    /// <summary>
    ///     Gets or sets the reference to the project configuration associated with this C# project.
    /// </summary>
    public LocalProjectConfig? Configuration { get; set; }

    /// <summary>
    ///     Gets or sets the collection of deployments associated with this C# project.
    /// </summary>
    public ICollection<Deployment> Deployments { get; set; } = [];
}