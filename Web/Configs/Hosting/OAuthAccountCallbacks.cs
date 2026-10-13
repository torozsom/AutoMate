using System.Text.Json;
using Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Web.Configs;

/// <summary>
///     Links provider account tickets while preserving AutoMate's existing authentication cookie and token exchange order.
/// </summary>
internal static class OAuthAccountCallbacks
{
    /// <summary>OAuth state key preserving the current AutoMate owner.</summary>
    private const string AutoMateUserIdAuthProperty = "automate_user_id";

    /// <summary>Local dashboard destination after Azure account connection.</summary>
    private const string AzureConnectionRedirectUri = "/dashboard";

    /// <summary>Azure Resource Manager delegated token scope.</summary>
    internal const string AzureManagementScope = "https://management.azure.com/.default";

    /// <summary>Default organizational Microsoft authority.</summary>
    private const string DefaultMicrosoftAuthorityTenant = "organizations";

    /// <summary>
    ///     Resolves the Microsoft identity authority tenant used for Azure account connection.
    /// </summary>
    /// <param name="configuredTenant">The optional tenant configured for local development or single-tenant installs.</param>
    /// <returns>The configured tenant, or a multi-tenant organizations authority by default.</returns>
    internal static string GetMicrosoftAuthorityTenant(string? configuredTenant)
    {
        if (string.IsNullOrWhiteSpace(configuredTenant))
            return DefaultMicrosoftAuthorityTenant;

        var tenant = configuredTenant.Trim();
        return tenant.Equals("common", StringComparison.OrdinalIgnoreCase) ||
               tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase) ||
               tenant.Equals("consumers", StringComparison.OrdinalIgnoreCase) ||
               Guid.TryParse(tenant, out _)
            ? tenant
            : DefaultMicrosoftAuthorityTenant;
    }


    /// <summary>
    ///     Persists the GitHub profile and access token from the completed OAuth ticket.
    /// </summary>
    /// <param name="context">OAuth ticket context with provider tokens; never log secret fields.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    internal static async Task ProcessGitHubLoginAsync(OAuthCreatingTicketContext context)
    {
        var githubId = context.User.GetProperty("id").GetInt64().ToString();
        var username = context.User.GetProperty("login").GetString() ?? "Unknown";
        var email = context.User.GetProperty("email").GetString();

        if (string.IsNullOrWhiteSpace(email))
            email = $"{githubId}@users.noreply.github.com";

        var avatarUrl = context.User.TryGetProperty("avatar_url", out var avatarElem)
            ? avatarElem.GetString()
            : null;

        var accessToken = context.AccessToken;

        var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();

        // Call domain service to persist user
        await authService.CreateOrUpdateGitHubUserAsync(
            githubId, username, email, avatarUrl, accessToken, context.HttpContext.RequestAborted);
    }


    /// <summary>
    ///     Extracts Microsoft identity data from the OAuth callback and links it to the current AutoMate
    ///     user.
    /// </summary>
    /// <param name="context">OAuth ticket context with provider tokens; never log secret fields.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    internal static async Task ProcessMicrosoftLoginAsync(OAuthCreatingTicketContext context)
    {
        if (!context.Properties.Items.TryGetValue(AutoMateUserIdAuthProperty, out var currentUserIdentifier) ||
            string.IsNullOrWhiteSpace(currentUserIdentifier))
            return;

        var idToken = GetTokenResponseString(context, "id_token");

        var azureAccountId = GetString(context.User, "sub")
                             ?? GetString(context.User, "oid")
                             ?? JwtPayloadReader.GetStringValue(idToken, "sub")
                             ?? JwtPayloadReader.GetStringValue(idToken, "oid")
                             ?? string.Empty;

        if (string.IsNullOrWhiteSpace(azureAccountId))
            return;

        var displayName = GetString(context.User, "name")
                          ?? JwtPayloadReader.GetStringValue(idToken, "name")
                          ?? "Azure user";

        var email = GetString(context.User, "email")
                    ?? GetString(context.User, "preferred_username")
                    ?? JwtPayloadReader.GetStringValue(idToken, "email")
                    ?? JwtPayloadReader.GetStringValue(idToken, "preferred_username")
                    ?? "no-email@microsoft.com";

        var tenantId = JwtPayloadReader.GetStringValue(idToken, "tid");
        var azureManagementToken = await ResolveAzureManagementTokenAsync(context);
        var subscriptionId = await AzureSubscriptionResolver.GetDefaultSubscriptionIdAsync(
            azureManagementToken,
            context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>(),
            context.HttpContext.RequestAborted);

        var expiresAt = GetTokenExpiresAt(context);

        var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();

        await authService.LinkAzureAccountAsync(
            currentUserIdentifier,
            azureAccountId,
            email,
            displayName,
            tenantId,
            subscriptionId,
            azureManagementToken,
            context.RefreshToken,
            expiresAt,
            context.HttpContext.RequestAborted);
    }


    /// <summary>
    ///     Prevents the Azure connect callback from replacing the existing AutoMate authentication
    ///     cookie.
    /// </summary>
    /// <param name="context">Completed OAuth ticket to reconcile with the existing AutoMate session.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    internal static Task CompleteMicrosoftConnectionAsync(TicketReceivedContext context)
    {
        if (context.Properties?.Items.ContainsKey(AutoMateUserIdAuthProperty) == true)
        {
            context.HandleResponse();
            context.Response.Redirect(AzureConnectionRedirectUri);
        }

        return Task.CompletedTask;
    }


    /// <summary>
    ///     Reads a string property from a JSON element.
    /// </summary>
    /// <param name="source">Selected diagnostic source flag or JSON object.</param>
    /// <param name="propertyName">JSON property or JWT claim name.</param>
    /// <returns>The JSON string property, or null when absent or explicitly null.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The JSON value is not an object or the selected property is neither a string nor null.
    /// </exception>
    private static string? GetString(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }


    /// <summary>
    ///     Reads a string value from the OAuth token response payload.
    /// </summary>
    /// <param name="context">OAuth ticket context with provider tokens; never log secret fields.</param>
    /// <param name="propertyName">JSON property or JWT claim name.</param>
    /// <returns>The token-response string property, or null when the response/property is absent or explicitly null.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The response root is not an object or the selected property is neither a string nor null.
    /// </exception>
    private static string? GetTokenResponseString(OAuthCreatingTicketContext context, string propertyName)
    {
        return context.TokenResponse.Response?.RootElement.TryGetProperty(propertyName, out var property) == true
            ? property.GetString()
            : null;
    }


    /// <summary>
    ///     Calculates the access token expiry time from the OAuth token response.
    /// </summary>
    /// <param name="context">OAuth ticket context with provider tokens; never log secret fields.</param>
    /// <returns>
    ///     The current UTC time plus the integer expires_in duration, or null when absent or not
    ///     representable as an integer.
    /// </returns>
    /// <exception cref="InvalidOperationException">The token response or expiry property has an incompatible JSON kind.</exception>
    private static DateTimeOffset? GetTokenExpiresAt(OAuthCreatingTicketContext context)
    {
        return context.TokenResponse.Response?.RootElement.TryGetProperty("expires_in", out var expiresInElement) ==
               true &&
               expiresInElement.TryGetInt32(out var expiresIn)
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : null;
    }


    /// <summary>
    ///     Exchanges the OAuth refresh token for an Azure Resource Manager-scoped access token.
    /// </summary>
    /// <param name="context">OAuth ticket context with provider tokens; never log secret fields.</param>
    /// <returns>
    ///     A task returning the ARM access token, or null for missing connection settings, a rejected
    ///     exchange or an absent token.
    /// </returns>
    /// <exception cref="HttpRequestException">The token endpoint cannot be reached.</exception>
    /// <exception cref="System.Text.Json.JsonException">The token response is malformed JSON.</exception>
    /// <exception cref="OperationCanceledException">The HTTP request is canceled or times out.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Required services are unavailable or token fields have incompatible JSON
    ///     kinds.
    /// </exception>
    private static async Task<string?> ResolveAzureManagementTokenAsync(OAuthCreatingTicketContext context)
    {
        var refreshToken = context.RefreshToken;
        var tokenEndpoint = context.Options.TokenEndpoint;
        var clientId = context.Options.ClientId;
        var clientSecret = context.Options.ClientSecret;

        if (string.IsNullOrWhiteSpace(refreshToken) ||
            string.IsNullOrWhiteSpace(tokenEndpoint) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
            return null;

        var httpClientFactory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        using var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = AzureManagementScope
        });

        using var response = await httpClient.SendAsync(request, context.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);

        using var payload = await JsonDocument.ParseAsync(stream,
            cancellationToken: context.HttpContext.RequestAborted);

        return payload.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
    }
}
