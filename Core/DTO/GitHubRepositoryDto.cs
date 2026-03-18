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
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("private")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}