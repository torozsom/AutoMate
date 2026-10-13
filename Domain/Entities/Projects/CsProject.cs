namespace Domain.Entities;

/// <summary>
///     Represents a C# project within a solution.
/// </summary>
public class CsProject : BaseEntity
{
    /// <summary>The unique identifier of the project associated with this C# project.</summary>
    public Guid AppId { get; set; }

    /// <summary>The reference to the project associated with this C# project.</summary>
    public Application Application { get; set; } = null!;

    /// <summary>The name of the C# application.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The path to the C# project.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Whether the C# application is a web project.</summary>
    public bool IsWebProject { get; set; }

    /// <summary>The reference to the project configuration associated with this C# project.</summary>
    public Configuration? Configuration { get; set; }

    /// <summary>The collection of deployments associated with this C# project.</summary>
    public ICollection<Deployment> Deployments { get; set; } = [];
}
