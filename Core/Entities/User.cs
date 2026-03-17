namespace Core.Entities;

/// <summary>
///     Represents a user of the application.
/// </summary>
public abstract class User
{
    /// The unique identifier for the user.
    public Guid Id { get; set; }

    /// The username to display for the user.
    public required string Username { get; set; }

    /// The email address of the user, which is required for the application.
    public required string Email { get; set; }

    /// The timestamp when the user was created, defaulting to the current UTC time.
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// A collection of projects associated with the user.
    public ICollection<Project> Projects { get; set; } = [];
}