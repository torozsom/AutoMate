using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Services.Auth;
using Web.Configs;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for connecting an Azure account to the current AutoMate user.
/// </summary>
public sealed class AzureLoginEndpoint : IEndpoint
{
    private const string AzureConnectionRedirectUri = "/dashboard";
    private const string AutoMateUserIdAuthProperty = "automate_user_id";
    private const string AzureManagementScope = "https://management.azure.com/.default";
    private const string StateCacheKeyPrefix = "azure-oauth-state:";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
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
                string? state = null, string? error = null, string? error_description = null) =>
            {
                if (!string.IsNullOrWhiteSpace(error))
                    return Results.Redirect(BuildDashboardRedirect("azure_error", error_description ?? error));

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
    ///     Starts a tenant-specific Microsoft OAuth authorization code flow for personal Microsoft-backed Azure tenants.
    /// </summary>
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
                ["prompt"] = "select_account"
            });

        return Results.Redirect(authorizationUrl);
    }


    /// <summary>
    ///     Exchanges the authorization code for ARM-scoped Azure tokens.
    /// </summary>
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
    private static string BuildAbsoluteCallbackUri(HttpContext context)
    {
        return $"{context.Request.Scheme}://{context.Request.Host}/api/auth/azure-login/callback";
    }


    /// <summary>
    ///     Generates a cryptographically random state token for the tenant-specific Azure OAuth flow.
    /// </summary>
    private static string GenerateStateToken()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }


    /// <summary>
    ///     Builds the distributed cache key used to store tenant-specific OAuth state.
    /// </summary>
    private static string GetStateCacheKey(string state)
    {
        return $"{StateCacheKeyPrefix}{state}";
    }


    /// <summary>
    ///     Builds a dashboard redirect URL that carries an Azure connection status message.
    /// </summary>
    private static string BuildDashboardRedirect(string key, string value)
    {
        return QueryHelpers.AddQueryString(AzureConnectionRedirectUri, key, value);
    }


    /// <summary>
    ///     Tenant-specific Azure OAuth state persisted between authorization and callback.
    /// </summary>
    private sealed record AzureOAuthState(string UserIdentifier, string TenantId, string RedirectUri);


    /// <summary>
    ///     Token response returned by the Microsoft identity platform.
    /// </summary>
    private sealed record AzureTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }

        [JsonPropertyName("id_token")] public string? IdToken { get; init; }

        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    }
}