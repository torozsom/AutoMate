using System.Threading.RateLimiting;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Services.Auth;
using Services.Azure;
using Services.Data;
using Services.Data.Apps;
using Services.Data.Users;
using Services.Docker;
using Services.Email;
using Services.GitHub;
using Services.LogStreaming;
using Services.Orchestration;
using Services.Scanner;
using Services.Templating;
using Web.Extensions;
using Web.Services;

namespace Web.Configs;

/// <summary>
///     Provides a centralized, modular configuration for ASP.NET Core dependency injection.
///     Ensures separation of concerns by splitting infrastructure, security, and presentation setups.
/// </summary>
public static class ServiceConfiguration
{
    private const string DefaultConnectionKey = "DefaultConnection";
    private const string RedisConnectionKey = "Redis";
    private const string AppName = "AutoMate";
    private const string AutoMateUserIdAuthProperty = "automate_user_id";
    private const string AzureConnectionRedirectUri = "/dashboard";
    private const string AzureManagementScope = "https://management.azure.com/.default";
    private const string AzureSubscriptionsApiVersion = "2022-12-01";


    /// <summary>
    ///     Helper method to extract GitHub OAuth mapping logic, improving readability and testability.
    /// </summary>
    private static async Task ProcessGitHubLoginAsync(OAuthCreatingTicketContext context)
    {
        var githubId = context.User.GetProperty("id").GetInt32().ToString();
        var username = context.User.GetProperty("login").GetString() ?? "Unknown";
        var email = context.User.GetProperty("email").GetString() ?? "no-email@github.com";

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
    ///     Extracts Microsoft identity data from the OAuth callback and links it to the current AutoMate user.
    /// </summary>
    private static async Task ProcessMicrosoftLoginAsync(OAuthCreatingTicketContext context)
    {
        if (!context.Properties.Items.TryGetValue(AutoMateUserIdAuthProperty, out var currentUserIdentifier) ||
            string.IsNullOrWhiteSpace(currentUserIdentifier))
            return;

        var idToken = GetTokenResponseString(context, "id_token");

        var azureAccountId = GetString(context.User, "sub")
                             ?? GetString(context.User, "oid")
                             ?? GetJwtPayloadValue(idToken, "sub")
                             ?? GetJwtPayloadValue(idToken, "oid")
                             ?? string.Empty;

        if (string.IsNullOrWhiteSpace(azureAccountId))
            return;

        var displayName = GetString(context.User, "name")
                          ?? GetJwtPayloadValue(idToken, "name")
                          ?? "Azure user";

        var email = GetString(context.User, "email")
                    ?? GetString(context.User, "preferred_username")
                    ?? GetJwtPayloadValue(idToken, "email")
                    ?? GetJwtPayloadValue(idToken, "preferred_username")
                    ?? "no-email@microsoft.com";

        var tenantId = GetJwtPayloadValue(idToken, "tid");
        var azureManagementToken = await ResolveAzureManagementTokenAsync(context);
        var subscriptionId = await GetDefaultSubscriptionIdAsync(
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
    ///     Prevents the Azure connect callback from replacing the existing AutoMate authentication cookie.
    /// </summary>
    private static Task CompleteMicrosoftConnectionAsync(TicketReceivedContext context)
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
    private static string? GetString(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }


    /// <summary>
    ///     Extracts a value from a JWT payload without validating the token.
    /// </summary>
    private static string? GetJwtPayloadValue(string? jwt, string propertyName)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 })
            return null;

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return GetString(json.RootElement, propertyName);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }


    /// <summary>
    ///     Reads a string value from the OAuth token response payload.
    /// </summary>
    private static string? GetTokenResponseString(OAuthCreatingTicketContext context, string propertyName)
    {
        return context.TokenResponse.Response?.RootElement.TryGetProperty(propertyName, out var property) == true
            ? property.GetString()
            : null;
    }


    /// <summary>
    ///     Calculates the access token expiry time from the OAuth token response.
    /// </summary>
    private static DateTimeOffset? GetTokenExpiresAt(OAuthCreatingTicketContext context)
    {
        return context.TokenResponse.Response?.RootElement.TryGetProperty("expires_in", out var expiresInElement) == true &&
               expiresInElement.TryGetInt32(out var expiresIn)
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : null;
    }


    /// <summary>
    ///     Exchanges the OAuth refresh token for an Azure Resource Manager-scoped access token.
    /// </summary>
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
        var payload = await JsonDocument.ParseAsync(stream, cancellationToken: context.HttpContext.RequestAborted);

        return payload.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
    }


    /// <summary>
    ///     Loads the first available Azure subscription ID for the connected account.
    /// </summary>
    private static async Task<string?> GetDefaultSubscriptionIdAsync(string? accessToken,
        IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://management.azure.com/subscriptions?api-version={AzureSubscriptionsApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!payload.RootElement.TryGetProperty("value", out var subscriptionsElement) ||
            subscriptionsElement.ValueKind != JsonValueKind.Array)
            return null;

        string? firstFallback = null;

        foreach (var subscription in subscriptionsElement.EnumerateArray())
        {
            if (!subscription.TryGetProperty("subscriptionId", out var idElement))
                continue;

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (firstFallback == null)
                firstFallback = id;

            var state = subscription.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString()
                : null;

            if (string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase))
                return id;
        }

        return firstFallback;
    }


    /// <summary>
    ///     Extension method to configure application services.
    /// </summary>
    /// <param name="builder">The WebApplicationBuilder used to configure services.</param>
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        ///     Bootstraps all application dependencies.
        /// </summary>
        /// <returns>The original WebApplicationBuilder for chaining.</returns>
        public WebApplicationBuilder AddApplicationServices()
        {
            // Add Logging Services
            builder.Services.AddLogging();

            // Add Health Checks
            builder.Services.AddHealthChecks();

            // Bind Strongly-Typed Configurations
            builder.AddConfigurations();

            // Add Infrastructure (DB, Redis, Clients)
            builder.AddInfrastructure();

            // Add Security (Auth, RateLimiting, DataProtection, Proxies)
            builder.AddSecurity();

            // Add Presentation Layer (Blazor, SignalR, Swagger)
            builder.AddPresentation();

            // Add Business Logic Services
            builder.Services.RegisterDomainServices();

            // Register Minimal API Endpoints
            builder.Services.AddEndpoints();

            return builder;
        }


        /// <summary>
        ///     Binds application settings to strongly-typed option classes using the Options Pattern.
        /// </summary>
        private void AddConfigurations()
        {
            builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
            builder.Services.Configure<DockerOptions>(builder.Configuration.GetSection(DockerOptions.SectionName));
        }


        /// <summary>
        ///     Registers database contexts, caching, and external HTTP clients.
        /// </summary>
        private void AddInfrastructure()
        {
            var services = builder.Services;
            var config = builder.Configuration;

            // PostgreSQL Setup with Connection Pooling
            services.AddDbContextPool<AutoMateDbContext>(options =>
                options.UseNpgsql(config.GetConnectionString(DefaultConnectionKey))
                    .UseSnakeCaseNamingConvention());

            // Redis for Distributed Caching
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = config.GetConnectionString(RedisConnectionKey);
                options.InstanceName = $"{AppName}_";
            });

            // External API Clients with Resilience
            services.AddHttpClient<IGitHubService, GitHubService>()
                .AddStandardResilienceHandler();
        }


        /// <summary>
        ///     Registers authentication, authorization, rate limiting, and security headers.
        /// </summary>
        /// <remarks>
        ///     IMPORTANT: Proxy configuration assumes the application is hosted behind a trusted reverse proxy.
        /// </remarks>
        private void AddSecurity()
        {
            var services = builder.Services;
            var config = builder.Configuration;

            // Proxy headers (Must run behind Nginx/Traefik/etc.)
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            // Add antiforgery services for CSRF protection
            services.AddAntiforgery();

            // Data Protection (Keeps cookies valid across container restarts)
            services.AddDataProtection()
                .PersistKeysToDbContext<AutoMateDbContext>()
                .SetApplicationName(AppName);

            // Authentication Setup
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = "GitHub";
                })
                .AddCookie()
                .AddGitHub(options =>
                {
                    options.ClientId = config["Authentication:GitHub:ClientId"]
                                       ?? throw new InvalidOperationException(
                                           "Authentication:GitHub:ClientId is missing from configuration.");
                    options.ClientSecret = config["Authentication:GitHub:ClientSecret"]
                                           ?? throw new InvalidOperationException(
                                               "Authentication:GitHub:ClientSecret is missing from configuration.");

                    options.CallbackPath = new PathString("/signin-github");
                    options.Scope.Add("user:email");
                    options.Scope.Add("repo");

                    options.Events.OnCreatingTicket = async context => await ProcessGitHubLoginAsync(context);
                })
                .AddOAuth("Microsoft", options =>
                {
                    var tenantId = config["Authentication:Microsoft:TenantId"] ?? "common";

                    options.ClientId = config["Authentication:Microsoft:ClientId"]
                                       ?? throw new InvalidOperationException(
                                           "Authentication:Microsoft:ClientId is missing from configuration.");
                    options.ClientSecret = config["Authentication:Microsoft:ClientSecret"]
                                           ?? throw new InvalidOperationException(
                                               "Authentication:Microsoft:ClientSecret is missing from configuration.");

                    options.CallbackPath = new PathString("/signin-microsoft");
                    options.AuthorizationEndpoint =
                        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize";
                    options.TokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.Scope.Add("offline_access");
                    options.Scope.Add(AzureManagementScope);

                    options.SaveTokens = true;

                    options.Events.OnCreatingTicket = async context => await ProcessMicrosoftLoginAsync(context);
                    options.Events.OnTicketReceived = CompleteMicrosoftConnectionAsync;
                });

            // Add a cascading authentication state provider
            services.AddCascadingAuthenticationState();
            services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

            // Rate Limiting Configured by IP or Authenticated User
            services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    var partitionKey = context.User.Identity?.IsAuthenticated == true
                        ? context.User.Identity.Name!
                        : context.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";

                    // Create a fixed window rate limiter that allows 100 requests per minute for each partition key.
                    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    });
                });
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            });
        }

        /// <summary>
        ///     Registers UI and API presentation dependencies.
        /// </summary>
        private void AddPresentation()
        {
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddSignalR();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
        }
    }


    /// <summary>
    ///     Extension method to register core services.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    extension(IServiceCollection services)
    {
        /// <summary>
        ///     Registers core domain and application services into the DI container.
        /// </summary>
        private void RegisterDomainServices()
        {
            // Core/Auth Services
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();

            // Orchestration & Docker
            services.AddScoped<IDockerService, DockerService>();
            services.AddScoped<ILocalDeploymentOrchestrator, LocalDeploymentOrchestrator>();
            services.AddScoped<ICloudDeploymentOrchestrator, CloudDeploymentOrchestrator>();
            services.AddScoped<IAzureDeploymentOrchestrator, AzureDeploymentOrchestrator>();
            services.AddScoped<IAzureContainerAppRuntimeStreamer, AzureContainerAppRuntimeStreamer>();
            services.AddSingleton<IDeploymentStatusNotifier, DeploymentStatusNotifier>();
            services.AddHostedService<DeploymentCleanupHostedService>();
            services.AddScoped<ILogStreamer, RealTimeLogStreamer>();

            // Business & Utilities
            services.AddScoped<IApplicationService, ApplicationService>();
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<ILocalSystemScannerService, LocalSystemScannerService>();
            services.AddScoped<IProjectScannerService, ProjectScannerService>();
            services.AddScoped<ITemplatingService, TemplatingService>();
            services.AddScoped<IEmailSenderService, GmailSenderService>();
        }
    }
}
