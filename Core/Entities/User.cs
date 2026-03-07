namespace Core.Entities;


/// <summary>
/// Represents a user of the application, identified by their GitHub username and email.
/// </summary>
public class User
{
    /// The unique identifier for the user.
    public Guid Id { get; set; }

    /// The GitHub username of the user, which is required for the application.
    public required string GitHubUsername { get; set; }

    /// The email address of the user, which is required for the application.
    public required string Email { get; set; }

    /// An optional GitHub access token for the user.
    public string? GitHubAccessToken { get; set; }

    /// A collection of projects associated with the user.
    public ICollection<Project> Projects { get; set; } = [];
}