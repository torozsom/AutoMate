using System.Security.Claims;
using Application.Data.Users;
using Microsoft.AspNetCore.Components.Authorization;

namespace Web.Components.Pages;

/// <summary>
///     Resolves AutoMate user records from Blazor authentication claims for page components.
/// </summary>
internal static class AuthenticatedUserResolver
{
    /// <summary>
    ///     Resolves the internal AutoMate user ID for the current authentication state.
    /// </summary>
    /// <param name="authStateProvider">The Blazor authentication state provider.</param>
    /// <param name="userService">The user service used for GitHub account ID fallback lookup.</param>
    /// <param name="cancellationToken">A token that cancels user lookups when the component is disposed.</param>
    /// <returns>A task returning the internal owner ID, or Guid.Empty for an anonymous or unresolved owner.</returns>
    /// <exception cref="OperationCanceledException">A required provider-account lookup is canceled.</exception>
    internal static async Task<Guid> GetCurrentUserIdAsync(
        AuthenticationStateProvider authStateProvider,
        IUserService userService,
        CancellationToken cancellationToken = default)
    {
        var identifier = await GetCurrentUserIdentifierAsync(authStateProvider);
        if (string.IsNullOrWhiteSpace(identifier))
            return Guid.Empty;

        if (Guid.TryParse(identifier, out var parsedId))
            return parsedId;

        // GitHub-authenticated users carry the provider account ID in the name identifier claim.
        return await userService.GetUserIdByGithubAccountIdAsync(identifier, cancellationToken);
    }


    /// <summary>
    ///     Resolves the current AutoMate user details, including remote-provider access tokens when
    ///     available.
    /// </summary>
    /// <param name="authStateProvider">The Blazor authentication state provider.</param>
    /// <param name="userService">The user service used to resolve stored AutoMate user details.</param>
    /// <param name="cancellationToken">A token that cancels user lookups when the component is disposed.</param>
    /// <returns>A task returning owner details and optional GitHub credentials, or an empty anonymous record.</returns>
    /// <exception cref="OperationCanceledException">The owner-details lookup is canceled.</exception>
    internal static async Task<AuthenticatedUserDetails> GetCurrentUserDetailsAsync(
        AuthenticationStateProvider authStateProvider,
        IUserService userService,
        CancellationToken cancellationToken = default)
    {
        var identifier = await GetCurrentUserIdentifierAsync(authStateProvider);
        if (string.IsNullOrWhiteSpace(identifier))
            return new AuthenticatedUserDetails(Guid.Empty, null, false);

        var (userId, accessToken, isGitHubUser) =
            await userService.GetUserDetailsFromIdentifierAsync(identifier, cancellationToken);

        return new AuthenticatedUserDetails(userId, accessToken, isGitHubUser);
    }


    /// <summary>
    ///     Reads the authenticated user's stable name identifier claim.
    /// </summary>
    /// <param name="authStateProvider">The Blazor authentication state provider.</param>
    /// <returns>A task returning the authenticated name identifier, or null for an anonymous principal.</returns>
    private static async Task<string?> GetCurrentUserIdentifierAsync(AuthenticationStateProvider authStateProvider)
    {
        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        return user.Identity?.IsAuthenticated == true
            ? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            : null;
    }
}
