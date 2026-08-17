using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.ResourceManager.ManagedServiceIdentities;
using Microsoft.Extensions.Logging;

namespace Services.Azure;

/// <summary>
///     Creates and verifies Azure managed identity federated credentials for GitHub Actions OIDC.
/// </summary>
internal sealed class AzureFederatedCredentialService(
    IHttpClientFactory httpClientFactory,
    ILogger logger)
{
    /// <summary>
    ///     Maximum number of read-back attempts after creating a federated credential.
    /// </summary>
    private const int FederatedCredentialReadinessAttempts = 6;

    /// <summary>
    ///     Ensures the federated credential exists and performs a best-effort readiness check.
    /// </summary>
    public async Task EnsureAsync(UserAssignedIdentityResource identity, string credentialName, string subject,
        string accessToken, CancellationToken cancellationToken)
    {
        await CreateOrUpdateAsync(identity, credentialName, subject, accessToken, cancellationToken);
        await WaitForReadinessAsync(identity, credentialName, subject, accessToken, cancellationToken);
    }

    /// <summary>
    ///     Creates or updates the federated credential with GitHub's issuer, subject, and token audience.
    /// </summary>
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
    ///     Polls ARM until the expected federated credential values can be read back or local retries expire.
    /// </summary>
    private async Task WaitForReadinessAsync(UserAssignedIdentityResource identity, string credentialName,
        string subject, string accessToken, CancellationToken cancellationToken)
    {
        string? lastIssuer = null;
        string? lastSubject = null;
        string? lastAudiences = null;

        for (var attempt = 1; attempt <= FederatedCredentialReadinessAttempts; attempt++)
        {
            try
            {
                var credential = await GetPropertiesAsync(identity, credentialName, accessToken, cancellationToken);
                lastIssuer = credential.Issuer;
                lastSubject = credential.Subject;
                lastAudiences = string.Join(", ", credential.Audiences);

                if (string.Equals(credential.Issuer, AzureConstants.GitHubTokenIssuer, StringComparison.Ordinal) &&
                    string.Equals(credential.Subject, subject, StringComparison.Ordinal) &&
                    credential.Audiences.Contains(AzureConstants.AzureTokenExchangeAudience, StringComparer.Ordinal))
                    return;
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex,
                    "[AzureDeploymentOrchestrator] Federated credential {CredentialName} was not readable on attempt {Attempt}.",
                    credentialName, attempt);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex,
                    "[AzureDeploymentOrchestrator] Federated credential {CredentialName} was not readable on attempt {Attempt}.",
                    credentialName, attempt);
            }

            if (attempt < FederatedCredentialReadinessAttempts)
                await Task.Delay(AzureConstants.PollDelay, cancellationToken);
        }

        logger.LogWarning(
            "[AzureDeploymentOrchestrator] Federated credential {CredentialName} was created, but ARM did not read back the expected OIDC issuer/subject/audience before the local readiness timeout. Continuing because Azure may still be propagating the credential. Expected issuer: {ExpectedIssuer}. Actual issuer: {ActualIssuer}. Expected subject: {ExpectedSubject}. Actual subject: {ActualSubject}. Expected audience: {ExpectedAudience}. Actual audiences: {ActualAudiences}.",
            credentialName, AzureConstants.GitHubTokenIssuer, lastIssuer ?? "<unavailable>", subject,
            lastSubject ?? "<unavailable>", AzureConstants.AzureTokenExchangeAudience,
            lastAudiences ?? "<unavailable>");
    }

    /// <summary>
    ///     Reads federated credential properties from ARM for readiness verification.
    /// </summary>
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
    private sealed record FederatedCredentialProperties(string Issuer, string Subject, IReadOnlyList<string> Audiences);
}