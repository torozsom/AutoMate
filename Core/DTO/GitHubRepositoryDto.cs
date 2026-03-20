using System.Text.Json.Serialization;

namespace Core.DTO;


/// <summary>
///     Data Transfer Object representing a GitHub repository.
///     This class is used to deserialize JSON responses from the GitHub API
///     when fetching repository information. It includes properties such as
///     the repository's ID, name, full name, URL, privacy status,
///     primary programming language, and last updated timestamp.
/// </summary>
public class GitHubRepositoryDto
{
    /// <summary>
    /// Gets or sets the unique identifier of the repository.
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the name of the repository.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full name of the repository, including the owner's name.
    /// </summary>
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL to view the repository on GitHub.
    /// </summary>
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the repository is private.
    /// </summary>
    [JsonPropertyName("private")]
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Gets or sets the primary programming language of the repository.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the repository was last updated.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}