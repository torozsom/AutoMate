using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Azure;

/// <summary>
///     Ensures Azure resource providers required by the selected deployment configuration are registered.
/// </summary>
internal sealed class AzureResourceProviderRegistrar(
    IHttpClientFactory httpClientFactory,
    ILogger logger)
{
    /// <summary>
    ///     Maximum number of provider registration polling attempts before treating setup as failed.
    /// </summary>
    private const int ProviderRegistrationAttempts = 24;

    /// <summary>
    ///     Providers required for every Azure Container Apps deployment.
    /// </summary>
    private static readonly string[] BaseRequiredResourceProviders =
    [
        "Microsoft.App",
        "Microsoft.OperationalInsights"
    ];

    /// <summary>
    ///     Registers all base and database-specific Azure providers needed for the deployment.
    /// </summary>
    public async Task EnsureRequiredProvidersAsync(AzureCloudCredentialsDto credentials, DeploymentConfigDto config,
        CancellationToken cancellationToken)
    {
        foreach (var providerNamespace in GetRequiredResourceProviders(config))
            await EnsureRegisteredAsync(credentials.SubscriptionId, providerNamespace, credentials.AccessToken,
                cancellationToken);
    }

    /// <summary>
    ///     Registers one provider namespace when it is not already ready on the subscription.
    /// </summary>
    private async Task EnsureRegisteredAsync(string subscriptionId, string providerNamespace, string accessToken,
        CancellationToken cancellationToken)
    {
        var registrationState = await GetRegistrationStateAsync(subscriptionId, providerNamespace, accessToken,
            cancellationToken);

        if (string.Equals(registrationState, AzureConstants.RegisteredProviderState,
                StringComparison.OrdinalIgnoreCase))
            return;

        using (var request = CreateRequest(subscriptionId, providerNamespace, accessToken, HttpMethod.Post,
                   "/register"))
        using (var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(
                        $"AutoMate cannot register Azure resource provider '{providerNamespace}' on subscription '{subscriptionId}'. " +
                        "Register it manually in Azure Portal, or connect with an Azure account that can register resource providers at subscription scope. Azure response: " +
                        responseText);

                response.EnsureSuccessStatusCode();
            }
        }

        await WaitForRegistrationAsync(subscriptionId, providerNamespace, accessToken, cancellationToken);
    }

    /// <summary>
    ///     Polls Azure until provider registration reaches the registered state.
    /// </summary>
    private async Task WaitForRegistrationAsync(string subscriptionId, string providerNamespace, string accessToken,
        CancellationToken cancellationToken)
    {
        string? registrationState = null;

        for (var attempt = 1; attempt <= ProviderRegistrationAttempts; attempt++)
        {
            registrationState = await GetRegistrationStateAsync(subscriptionId, providerNamespace, accessToken,
                cancellationToken);

            if (string.Equals(registrationState, AzureConstants.RegisteredProviderState,
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "[AzureDeploymentOrchestrator] Azure resource provider {ProviderNamespace} is registered on subscription {SubscriptionId}.",
                    providerNamespace, subscriptionId);
                return;
            }

            if (attempt < ProviderRegistrationAttempts)
                await Task.Delay(AzureConstants.PollDelay, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Azure resource provider '{providerNamespace}' registration did not complete before the timeout. Current state: {registrationState ?? "<unknown>"}.");
    }

    /// <summary>
    ///     Reads the current registration state for a provider namespace.
    /// </summary>
    private async Task<string?> GetRegistrationStateAsync(string subscriptionId, string providerNamespace,
        string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(subscriptionId, providerNamespace, accessToken, HttpMethod.Get);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return payload.RootElement.TryGetProperty("registrationState", out var registrationState)
            ? registrationState.GetString()
            : null;
    }

    /// <summary>
    ///     Derives the provider namespaces required by the configured database dependencies.
    /// </summary>
    private static IReadOnlyCollection<string> GetRequiredResourceProviders(DeploymentConfigDto config)
    {
        var providers = new HashSet<string>(BaseRequiredResourceProviders, StringComparer.OrdinalIgnoreCase);

        foreach (var database in config.Databases)
            switch (database.DbType.Trim().ToLowerInvariant())
            {
                case "postgresql":
                case "postgres":
                    providers.Add("Microsoft.DBforPostgreSQL");
                    break;
                case "mysql":
                    providers.Add("Microsoft.DBforMySQL");
                    break;
                case "sqlserver":
                case "sql-server":
                case "mssql":
                case "microsoft sql server":
                    providers.Add("Microsoft.Sql");
                    break;
                case "mongodb":
                case "mongo":
                    providers.Add("Microsoft.DocumentDB");
                    break;
                case "redis":
                    providers.Add("Microsoft.Cache");
                    break;
            }

        return providers;
    }

    /// <summary>
    ///     Builds an authenticated ARM request for provider registration operations.
    /// </summary>
    private static HttpRequestMessage CreateRequest(string subscriptionId, string providerNamespace,
        string accessToken, HttpMethod method, string suffix = "")
    {
        var requestUri =
            $"{AzureConstants.ManagementEndpoint}/subscriptions/{Uri.EscapeDataString(subscriptionId)}/providers/{Uri.EscapeDataString(providerNamespace)}{suffix}?api-version={AzureConstants.ResourceProviderApiVersion}";

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}