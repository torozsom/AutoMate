namespace Core.Entities;

/// <summary>
///     Represents a user of the application.
/// </summary>
public abstract class User : BaseEntity
{
    /// <summary>
    ///     Gets or sets the username of the user.
    /// </summary>
    public required string Username { get; set; }

    /// <summary>
    ///     Gets or sets the email address of the user.
    /// </summary>
    public required string Email { get; set; }

    /// <summary>
    ///     Gets or sets the collection of projects owned by the user.
    /// </summary>
    public ICollection<Project> Projects { get; set; } = [];
}