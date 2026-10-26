using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.ResourceManager.ManagedServiceIdentities;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Azure.Identity;

/// <summary>
///     Creates and verifies Azure managed identity federated credentials for GitHub Actions
///     OIDC.
/// </summary>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class AzureFederatedCredentialService(
    IHttpClientFactory httpClientFactory,
    ILogger logger)
{
    /// <summary>Maximum number of read-back attempts after creating a federated credential.</summary>
    private const int FederatedCredentialReadinessAttempts = 6;

    /// <summary>
    ///     Ensures the federated credential exists and performs a best-effort readiness check.
    /// </summary>
    /// <param name="identity">Verified user-assigned Azure identity.</param>
    /// <param name="credentialName">Managed federated credential name.</param>
    /// <param name="subject">The email subject.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public async Task EnsureAsync(UserAssignedIdentityResource identity, string credentialName, string subject,
        string accessToken, CancellationToken cancellationToken)
    {
        await CreateOrUpdateAsync(identity, credentialName, subject, accessToken, cancellationToken);
        await WaitForReadinessAsync(identity, credentialName, subject, accessToken, cancellationToken);
    }

    /// <summary>
    ///     Creates or updates the federated credential with GitHub's issuer, subject, and token
    ///     audience.
    /// </summary>
    /// <param name="identity">Verified user-assigned Azure identity.</param>
    /// <param name="credentialName">Managed federated credential name.</param>
    /// <param name="subject">The email subject.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Azure rejects creation or update of the required federated trust.
    /// </exception>
    private async Task CreateOrUpdateAsync(UserAssignedIdentityResource identity, string credentialName, string subject,
        string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(identity, credentialName, accessToken, HttpMethod.Put);
        request.Content = JsonContent.Create(new
        {
            properties = new
            {
                issuer = AzureConstants.GitHubTokenIssuer,
                subject,
                audiences = new[] { AzureConstants.AzureTokenExchangeAudience }
            }
        });

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Azure federated credential '{credentialName}' could not be created or updated. Azure response: {responseText}");
    }

    /// <summary>
    ///     Polls ARM until the expected federated credential values can be read back or local
    ///     retries expire.
    /// </summary>
    /// <param name="identity">Verified user-assigned Azure identity.</param>
    /// <param name="credentialName">Managed federated credential name.</param>
    /// <param name="subject">The email subject.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task WaitForReadinessAsync(UserAssignedIdentityResource identity, string credentialName,
        string subject, string accessToken, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= FederatedCredentialReadinessAttempts; attempt++)
        {
            try
            {
                var credential = await GetPropertiesAsync(identity, credentialName, accessToken, cancellationToken);

                if (string.Equals(credential.Issuer, AzureConstants.GitHubTokenIssuer, StringComparison.Ordinal) &&
                    string.Equals(credential.Subject, subject, StringComparison.Ordinal) &&
                    credential.Audiences.Contains(AzureConstants.AzureTokenExchangeAudience, StringComparer.Ordinal))
                    return;
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug("Azure federated credential was not readable on attempt {Attempt}: {FailureType}.",
                    attempt, ex.GetType().Name);
            }
            catch (JsonException ex)
            {
                logger.LogDebug("Azure federated credential was not readable on attempt {Attempt}: {FailureType}.",
                    attempt, ex.GetType().Name);
            }

            if (attempt < FederatedCredentialReadinessAttempts)
                await Task.Delay(AzureConstants.PollDelay, cancellationToken);
        }

        logger.LogWarning(
            "Azure federated credential readiness check timed out after {AttemptCount} attempts; continuing while Azure propagates the credential.",
            FederatedCredentialReadinessAttempts);
    }

    /// <summary>
    ///     Reads federated credential properties from ARM for readiness verification.
    /// </summary>
    /// <param name="identity">Verified user-assigned Azure identity.</param>
    /// <param name="credentialName">Managed federated credential name.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the federated credential properties projection derived from the
    ///     verified inputs.
    /// </returns>
    private async Task<FederatedCredentialProperties> GetPropertiesAsync(
        UserAssignedIdentityResource identity, string credentialName, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(identity, credentialName, accessToken, HttpMethod.Get);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var properties = payload.RootElement.GetProperty("properties");

        var audiences = properties.GetProperty("audiences")
            .EnumerateArray()
            .Select(audience => audience.GetString())
            .Where(audience => !string.IsNullOrWhiteSpace(audience))
            .Cast<string>()
            .ToArray();

        return new FederatedCredentialProperties(
            properties.GetProperty("issuer").GetString() ?? string.Empty,
            properties.GetProperty("subject").GetString() ?? string.Empty,
            audiences);
    }

    /// <summary>
    ///     Builds an authenticated ARM request for one managed identity federated credential.
    /// </summary>
    /// <param name="identity">Verified user-assigned Azure identity.</param>
    /// <param name="credentialName">Managed federated credential name.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="method">HTTP method used for the bounded provider request.</param>
    /// <returns>The configured request with bounded provider headers and protected authorization.</returns>
    private static HttpRequestMessage CreateRequest(UserAssignedIdentityResource identity,
        string credentialName, string accessToken, HttpMethod method)
    {
        var requestUri =
            $"{AzureConstants.ManagementEndpoint}{identity.Id}/federatedIdentityCredentials/{Uri.EscapeDataString(credentialName)}?api-version={AzureConstants.ManagedIdentityFederatedCredentialApiVersion}";

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    /// <summary>
    ///     ARM federated credential properties needed for readiness checks.
    /// </summary>
    /// <param name="Issuer">OIDC token issuer trusted by the configured identity.</param>
    /// <param name="Subject">The email subject.</param>
    /// <param name="Audiences">OIDC audiences accepted by the federated trust.</param>
    private sealed record FederatedCredentialProperties(string Issuer, string Subject, IReadOnlyList<string> Audiences);
}
