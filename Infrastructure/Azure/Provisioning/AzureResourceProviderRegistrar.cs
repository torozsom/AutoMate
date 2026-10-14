using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Domain.DTO.Azure;
using Domain.DTO.Deployment;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Azure.Provisioning;

/// <summary>
///     Ensures Azure resource providers required by the selected deployment configuration are
///     registered.
/// </summary>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class AzureResourceProviderRegistrar(
    IHttpClientFactory httpClientFactory,
    ILogger logger)
{
    /// <summary>
    ///     Maximum number of provider registration polling attempts before treating setup as
    ///     failed.
    /// </summary>
    private const int ProviderRegistrationAttempts = 24;

    /// <summary>Providers required for every Azure Container Apps deployment.</summary>
    private static readonly string[] BaseRequiredResourceProviders =
    [
        "Microsoft.App",
        "Microsoft.Insights",
        "Microsoft.OperationalInsights"
    ];

    /// <summary>
    ///     Registers all base and database-specific Azure providers needed for the deployment.
    /// </summary>
    /// <param name="credentials">The connected Azure credentials used for resource preparation.</param>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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
    /// <param name="subscriptionId">The optional Azure subscription identifier.</param>
    /// <param name="providerNamespace">
    ///     Allowlisted Azure resource-provider namespace required by deployment assets.
    /// </param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Azure rejects a required provider registration or readiness cannot be confirmed.
    /// </exception>
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
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(
                        $"AutoMate cannot register Azure resource provider '{providerNamespace}'. " +
                        "Register it manually in Azure Portal, or connect with an Azure account that can register resource providers at subscription scope.");

                response.EnsureSuccessStatusCode();
            }
        }

        await WaitForRegistrationAsync(subscriptionId, providerNamespace, accessToken, cancellationToken);
    }

    /// <summary>
    ///     Polls Azure until provider registration reaches the registered state.
    /// </summary>
    /// <param name="subscriptionId">The optional Azure subscription identifier.</param>
    /// <param name="providerNamespace">
    ///     Allowlisted Azure resource-provider namespace required by deployment assets.
    /// </param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The required provider does not reach registered state within the bounded readiness window.
    /// </exception>
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
    /// <param name="subscriptionId">The optional Azure subscription identifier.</param>
    /// <param name="providerNamespace">
    ///     Allowlisted Azure resource-provider namespace required by deployment assets.
    /// </param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the registration state async derived from the validated input, or null
    ///     when no value is available.
    /// </returns>
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
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <returns>The required resource providers selection with its documented bounds and ordering.</returns>
    private static IReadOnlyCollection<string> GetRequiredResourceProviders(DeploymentConfigDto config)
    {
        var providers = new HashSet<string>(BaseRequiredResourceProviders, StringComparer.OrdinalIgnoreCase);
        if (config.CloudRegistryName.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase))
            providers.Add("Microsoft.ContainerRegistry");

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
    /// <param name="subscriptionId">The optional Azure subscription identifier.</param>
    /// <param name="providerNamespace">
    ///     Allowlisted Azure resource-provider namespace required by deployment assets.
    /// </param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="method">HTTP method used for the bounded provider request.</param>
    /// <param name="suffix">Stable component or artifact name suffix.</param>
    /// <returns>The configured request with bounded provider headers and protected authorization.</returns>
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
