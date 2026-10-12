namespace Domain.Entities;

/// <summary>
///     Represents a user of the application.
/// </summary>
public abstract class User : BaseEntity
{
    /// <summary>The username of the user.</summary>
    public required string Username { get; set; }

    /// <summary>The email address of the user.</summary>
    public required string Email { get; set; }

    /// <summary>The collection of applications owned by the user.</summary>
    public ICollection<Application> Applications { get; set; } = [];
}