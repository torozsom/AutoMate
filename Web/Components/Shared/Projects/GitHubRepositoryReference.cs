namespace Web.Components.Shared;

/// <summary>
///     Identifies a GitHub repository by owner and repository name.
/// </summary>
/// <param name="Owner">The GitHub user or organization that owns the repository.</param>
/// <param name="Name">The repository name without a trailing .git suffix.</param>
internal readonly record struct GitHubRepositoryReference(string Owner, string Name);