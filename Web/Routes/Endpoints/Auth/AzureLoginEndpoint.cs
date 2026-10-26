using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Web.Configs.Azure;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for connecting an Azure account to the current AutoMate user.
/// </summary>
public sealed class AzureLoginEndpoint : IEndpoint
{
    /// <summary>Local dashboard destination after Azure account connection.</summary>
    private const string AzureConnectionRedirectUri = "/dashboard";

    /// <summary>OAuth state key preserving the current AutoMate owner.</summary>
    private const string AutoMateUserIdAuthProperty = "automate_user_id";

    /// <summary>Azure Resource Manager delegated token scope.</summary>
    private const string AzureManagementScope = "https://management.azure.com/.default";

    /// <summary>Cache key prefix isolating Azure OAuth state.</summary>
    private const string StateCacheKeyPrefix = "azure-oauth-state:";

    /// <summary>Maximum lifetime of cached OAuth state.</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Web-compatible token/state JSON serialization settings.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Maps authorized Azure account connection and tenant-specific OAuth callback routes.
    /// </summary>
    /// <param name="app">Endpoint route builder receiving these routes.</param>
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/azure-login",
            async (HttpContext context, IConfiguration configuration, IDistributedCache cache,
                string? tenantId = null) =>
            {
                var userIdentifier = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (string.IsNullOrWhiteSpace(userIdentifier))
                    return Results.Forbid();

                if (!string.IsNullOrWhiteSpace(tenantId))
                    return await StartTenantSpecificAzureLoginAsync(context, configuration, cache, userIdentifier,
                        tenantId);

                var properties = new AuthenticationProperties
                {
                    RedirectUri = AzureConnectionRedirectUri,
                    Items = { [AutoMateUserIdAuthProperty] = userIdentifier }
                };

                return Results.Challenge(properties, ["Microsoft"]);
            }).RequireAuthorization();

        app.MapGet("/api/auth/azure-login/callback",
            async (HttpContext context, IConfiguration configuration, IDistributedCache cache,
                IHttpClientFactory httpClientFactory, IAuthService authService, string? code = null,
                string? state = null, string? error = null,
                [FromQuery(Name = "error_description")]
                string? errorDescription = null) =>
            {
                if (!string.IsNullOrWhiteSpace(error))
                    return Results.Redirect(BuildDashboardRedirect("azure_error", errorDescription ?? error));

                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                    return Results.BadRequest("Azure OAuth callback is missing code or state.");

                var stateJson = await cache.GetStringAsync(GetStateCacheKey(state), context.RequestAborted);

                if (string.IsNullOrWhiteSpace(stateJson))
                    return Results.BadRequest("Azure OAuth state is invalid or expired.");

                await cache.RemoveAsync(GetStateCacheKey(state), context.RequestAborted);

                var stateData = JsonSerializer.Deserialize<AzureOAuthState>(stateJson, JsonOptions);

                if (stateData == null || string.IsNullOrWhiteSpace(stateData.UserIdentifier) ||
                    string.IsNullOrWhiteSpace(stateData.TenantId))
                    return Results.BadRequest("Azure OAuth state payload is invalid.");

                AzureTokenResponse tokenResponse;

                try
                {
                    tokenResponse = await ExchangeAuthorizationCodeAsync(configuration, httpClientFactory,
                        stateData.TenantId, stateData.RedirectUri, code, context.RequestAborted);
                }
                catch (HttpRequestException)
                {
                    return Results.Redirect(BuildDashboardRedirect("azure_error",
                        "Azure rejected the tenant-specific sign-in. Check the tenant ID, redirect URI, app registration account type, and Azure Service Management permission."));
                }
                catch (JsonException)
                {
                    return Results.Redirect(BuildDashboardRedirect("azure_error",
                        "Azure returned an invalid token response for the connected account."));
                }

                if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                    return Results.Redirect(BuildDashboardRedirect("azure_error",
                        "Azure did not return an access token for the connected account."));

                var azureAccountId = JwtPayloadReader.GetStringValue(tokenResponse.IdToken, "oid")
                                     ?? JwtPayloadReader.GetStringValue(tokenResponse.IdToken, "sub")
                                     ?? string.Empty;

                if (string.IsNullOrWhiteSpace(azureAccountId))
                    return Results.Redirect(BuildDashboardRedirect("azure_error",
                        "Azure did not return an account identifier for the connected account."));

                var displayName = JwtPayloadReader.GetStringValue(tokenResponse.IdToken, "name")
                                  ?? "Azure user";

                var email = JwtPayloadReader.GetStringValue(tokenResponse.IdToken, "email")
                            ?? JwtPayloadReader.GetStringValue(tokenResponse.IdToken, "preferred_username")
                            ?? "no-email@microsoft.com";

                var tenantFromToken = JwtPayloadReader.GetStringValue(tokenResponse.IdToken, "tid");
                var subscriptionId = await AzureSubscriptionResolver.GetDefaultSubscriptionIdAsync(
                    tokenResponse.AccessToken,
                    httpClientFactory, context.RequestAborted);

                if (string.IsNullOrWhiteSpace(subscriptionId))
                    return Results.Redirect(BuildDashboardRedirect("azure_error",
                        "No Azure subscription was found for this account. Select the tenant that contains an active subscription."));

                var expiresAt = tokenResponse.ExpiresIn > 0
                    ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn)
                    : (DateTimeOffset?)null;

                await authService.LinkAzureAccountAsync(
                    stateData.UserIdentifier,
                    azureAccountId,
                    email,
                    displayName,
                    string.IsNullOrWhiteSpace(tenantFromToken) ? stateData.TenantId : tenantFromToken,
                    subscriptionId,
                    tokenResponse.AccessToken,
                    tokenResponse.RefreshToken,
                    expiresAt,
                    context.RequestAborted);

                return Results.Redirect(AzureConnectionRedirectUri);
            });
    }


    /// <summary>
    ///     Starts a tenant-specific Microsoft OAuth authorization code flow for personal Microsoft-backed
    ///     Azure tenants.
    /// </summary>
    /// <param name="context">Current authenticated HTTP request and response.</param>
    /// <param name="configuration">Deployment settings or host configuration used by this operation.</param>
    /// <param name="cache">Stores short-lived OAuth state.</param>
    /// <param name="userIdentifier">Authenticated AutoMate identity claim.</param>
    /// <param name="tenantId">Selected Microsoft tenant identity.</param>
    /// <returns>A task returning the HTTP outcome of the authorized operation.</returns>
    /// <exception cref="InvalidOperationException">Required host or OAuth configuration is missing.</exception>
    private static async Task<IResult> StartTenantSpecificAzureLoginAsync(HttpContext context,
        IConfiguration configuration, IDistributedCache cache, string userIdentifier, string tenantId)
    {
        if (!Guid.TryParse(tenantId.Trim(), out var parsedTenantId))
            return Results.BadRequest("A valid Azure tenant ID is required.");

        var clientId = configuration["Authentication:Microsoft:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Authentication:Microsoft:ClientId is missing from configuration.");

        var redirectUri = BuildAbsoluteCallbackUri(context);
        var state = GenerateStateToken();
        var stateData = new AzureOAuthState(userIdentifier, parsedTenantId.ToString(), redirectUri);

        await cache.SetStringAsync(GetStateCacheKey(state), JsonSerializer.Serialize(stateData, JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StateLifetime },
            context.RequestAborted);

        var authorizationUrl = QueryHelpers.AddQueryString(
            $"https://login.microsoftonline.com/{parsedTenantId}/oauth2/v2.0/authorize",
            new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["response_mode"] = "query",
                ["scope"] = $"openid profile email offline_access {AzureManagementScope}",
                ["state"] = state,
                // Reconnecting must include newly declared diagnostic permissions in static consent.
                ["prompt"] = "consent"
            });

        return Results.Redirect(authorizationUrl);
    }


    /// <summary>
    ///     Exchanges the authorization code for ARM-scoped Azure tokens.
    /// </summary>
    /// <param name="configuration">Deployment settings or host configuration used by this operation.</param>
    /// <param name="httpClientFactory">Creates the scoped Azure HTTP client.</param>
    /// <param name="tenantId">Selected Microsoft tenant identity.</param>
    /// <param name="redirectUri">Registered absolute OAuth callback URI.</param>
    /// <param name="code">Short-lived OAuth authorization code; never log it.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>A task returning the parsed token response; secret fields must never enter diagnostics.</returns>
    /// <exception cref="InvalidOperationException">Required host or OAuth configuration is missing.</exception>
    /// <exception cref="HttpRequestException">The token endpoint rejects the code exchange or cannot be reached.</exception>
    /// <exception cref="System.Text.Json.JsonException">The successful token response is malformed JSON.</exception>
    /// <exception cref="OperationCanceledException">The HTTP request is canceled or times out.</exception>
    private static async Task<AzureTokenResponse> ExchangeAuthorizationCodeAsync(IConfiguration configuration,
        IHttpClientFactory httpClientFactory, string tenantId, string redirectUri, string code,
        CancellationToken cancellationToken)
    {
        var clientId = configuration["Authentication:Microsoft:ClientId"];
        var clientSecret = configuration["Authentication:Microsoft:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Authentication:Microsoft:ClientId is missing from configuration.");

        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Authentication:Microsoft:ClientSecret is missing from configuration.");

        using var httpClient = httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token");

        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = $"openid profile email offline_access {AzureManagementScope}"
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<AzureTokenResponse>(JsonOptions,
            cancellationToken);

        return tokenResponse ?? new AzureTokenResponse();
    }


    /// <summary>
    ///     Builds the absolute callback URL registered with the Microsoft identity platform.
    /// </summary>
    /// <param name="context">Current authenticated HTTP request and response.</param>
    /// <returns>The absolute tenant-specific OAuth callback URL for this request.</returns>
    private static string BuildAbsoluteCallbackUri(HttpContext context)
    {
        return $"{context.Request.Scheme}://{context.Request.Host}/api/auth/azure-login/callback";
    }


    /// <summary>
    ///     Generates a cryptographically random state token for the tenant-specific Azure OAuth flow.
    /// </summary>
    /// <returns>A cryptographically random Base64Url OAuth state value.</returns>
    private static string GenerateStateToken()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }


    /// <summary>
    ///     Builds the distributed cache key used to store tenant-specific OAuth state.
    /// </summary>
    /// <param name="state">Cryptographically random OAuth state token.</param>
    /// <returns>The Azure OAuth prefix combined with the state token.</returns>
    private static string GetStateCacheKey(string state)
    {
        return $"{StateCacheKeyPrefix}{state}";
    }


    /// <summary>
    ///     Builds a dashboard redirect URL that carries an Azure connection status message.
    /// </summary>
    /// <param name="key">Fixed connection-status query key.</param>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>A local dashboard URL with escaped connection-status query values.</returns>
    private static string BuildDashboardRedirect(string key, string value)
    {
        return QueryHelpers.AddQueryString(AzureConnectionRedirectUri, key, value);
    }


    /// <summary>
    ///     Tenant-specific Azure OAuth state persisted between authorization and callback.
    /// </summary>
    /// <param name="UserIdentifier">Authenticated AutoMate identity claim.</param>
    /// <param name="TenantId">Selected Microsoft tenant identity.</param>
    /// <param name="RedirectUri">Registered absolute OAuth callback URI.</param>
    private sealed record AzureOAuthState(string UserIdentifier, string TenantId, string RedirectUri);


    /// <summary>
    ///     Token response returned by the Microsoft identity platform.
    /// </summary>
    private sealed record AzureTokenResponse
    {
        /// <summary>Provider access token, if returned; never log it.</summary>
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        /// <summary>Provider refresh token, if returned; never log it.</summary>
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        /// <summary>Identity token used to read provider account claims; never log it.</summary>
        [JsonPropertyName("id_token")]
        public string? IdToken { get; init; }

        /// <summary>Provider-reported access-token lifetime in seconds.</summary>
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
