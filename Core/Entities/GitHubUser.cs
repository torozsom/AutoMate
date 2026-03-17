namespace Core.Entities;

/// <summary>
///     Represents a user of the application, identified by their GitHub username and email.
/// </summary>
public class GitHubUser : User
{
    /// The unique identifier of the user on GitHub.
    public required string AccountId { get; set; }

    /// The URL of the user's avatar image on GitHub.'
    public string? AvatarUrl { get; set; }

    /// An optional GitHub access token for the user.
    public string? AccessToken { get; set; }
}