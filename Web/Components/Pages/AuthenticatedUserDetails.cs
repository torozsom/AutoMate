namespace Web.Components.Pages;

/// <summary>
///     Represents the current AutoMate user details resolved from the active authentication state.
/// </summary>
/// <param name="UserId">The internal AutoMate user identifier, or <see cref="Guid.Empty" /> when unresolved.</param>
/// <param name="AccessToken">The GitHub access token available for remote users, when present.</param>
/// <param name="IsGitHubUser">Indicates whether the authenticated identity came from GitHub.</param>
internal readonly record struct AuthenticatedUserDetails(Guid UserId, string? AccessToken, bool IsGitHubUser);