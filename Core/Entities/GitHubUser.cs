namespace Core.Entities;

/// <summary>
///     Represents a user of the application, identified by their GitHub username and email.
/// </summary>
public class GitHubUser : User
{
    /// <summary>
    /// Gets or sets the unique identifier of the user on GitHub.
    /// This is typically the GitHub user numeric ID or login handle stored as string.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    /// Gets or sets the URL of the user's avatar image on GitHub.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Gets or sets the GitHub access token associated with the user, if available.
    /// This token is used to authenticate requests to the GitHub API on behalf of the user.
    /// </summary>
    public string? AccessToken { get; set; }
}