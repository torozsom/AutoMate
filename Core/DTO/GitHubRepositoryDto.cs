using System.Text.Json.Serialization;

namespace Core.DTO;

/// <summary>
///     Represents the subset of GitHub repository metadata AutoMate needs for repository import.
/// </summary>
public record GitHubRepositoryDto
{
    /// <summary>
    ///     The GitHub repository identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>
    ///     The repository name without the owner.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     The owner/repository full name.
    /// </summary>
    [JsonPropertyName("full_name")]
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    ///     The browser URL for the repository on GitHub.
    /// </summary>
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Indicates whether the repository is private.
    /// </summary>
    [JsonPropertyName("private")]
    public bool IsPrivate { get; init; }

    /// <summary>
    ///     The primary language reported by GitHub, when available.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>
    ///     The timestamp when GitHub last updated the repository metadata.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}