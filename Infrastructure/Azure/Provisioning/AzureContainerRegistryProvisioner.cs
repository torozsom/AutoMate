using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.DTO.Azure;
using Domain.DTO.Deployment;

namespace Infrastructure.Azure.Provisioning;

/// <summary>
///     Ensures a customer-owned Basic ACR exists in the deployment resource group.
/// </summary>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
internal sealed class AzureContainerRegistryProvisioner(IHttpClientFactory httpClientFactory)
{
    /// <summary>
    ///     Gets or creates the requested registry and waits for ARM provisioning to finish.
    /// </summary>
    /// <param name="credentials">The connected Azure credentials used for resource preparation.</param>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the ensure async derived from the validated input.</returns>
    /// <exception cref="InvalidOperationException">
    ///     SaaS deployments require Azure Container Registry.
    /// </exception>
    /// <exception cref="TimeoutException">
    ///     Azure Container Registry provisioning did not finish in time.
    /// </exception>
    public async Task<string> EnsureAsync(AzureCloudCredentialsDto credentials, DeploymentConfigDto config,
        CancellationToken cancellationToken)
    {
        var server = config.CloudRegistryName.Trim().ToLowerInvariant();
        if (!server.EndsWith(".azurecr.io", StringComparison.Ordinal))
            throw new InvalidOperationException("SaaS deployments require Azure Container Registry.");
        var name = server[..^".azurecr.io".Length];
        var id = $"/subscriptions/{credentials.SubscriptionId}/resourceGroups/{config.CloudResourceGroupName}" +
                 $"/providers/Microsoft.ContainerRegistry/registries/{name}";
        var uri = $"https://management.azure.com{id}?api-version=2023-07-01";
        using var get = Request(HttpMethod.Get, uri, credentials.AccessToken);
        using var existing = await httpClientFactory.CreateClient().SendAsync(get, cancellationToken);
        if (existing.IsSuccessStatusCode)
        {
            await VerifyServerAsync(existing, server, cancellationToken);
            return id;
        }

        if (existing.StatusCode != HttpStatusCode.NotFound) existing.EnsureSuccessStatusCode();

        using var put = Request(HttpMethod.Put, uri, credentials.AccessToken);
        put.Content = JsonContent.Create(new
        {
            location = config.CloudAzureRegion,
            sku = new { name = "Basic" },
            properties = new { adminUserEnabled = false }
        });
        using var created = await httpClientFactory.CreateClient().SendAsync(put, cancellationToken);
        if (created.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException(
                "The Azure Container Registry name is unavailable or belongs to another resource group.");
        created.EnsureSuccessStatusCode();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var poll = Request(HttpMethod.Get, uri, credentials.AccessToken);
            using var result = await httpClientFactory.CreateClient().SendAsync(poll, cancellationToken);
            result.EnsureSuccessStatusCode();
            using var payload = await JsonDocument.ParseAsync(
                await result.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var state = payload.RootElement.GetProperty("properties")
                .GetProperty("provisioningState").GetString();
            if (state == "Succeeded")
            {
                var loginServer = payload.RootElement.GetProperty("properties")
                    .GetProperty("loginServer").GetString();
                if (!string.Equals(loginServer, server, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Azure returned a different Container Registry server.");
                return id;
            }

            if (state is "Failed" or "Canceled")
                throw new InvalidOperationException("Azure Container Registry provisioning failed.");
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        throw new TimeoutException("Azure Container Registry provisioning did not finish in time.");
    }

    /// <summary>
    ///     Checks that an existing registry matches the configured login server.
    /// </summary>
    /// <param name="response">The untrusted structured analysis response to validate.</param>
    /// <param name="server">Validated registry, mail or telemetry server.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The selected Azure registry does not match its login server.
    /// </exception>
    private static async Task VerifyServerAsync(HttpResponseMessage response, string server,
        CancellationToken cancellationToken)
    {
        using var payload = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var actual = payload.RootElement.GetProperty("properties").GetProperty("loginServer").GetString();
        if (!string.Equals(actual, server, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected Azure registry does not match its login server.");
    }

    /// <summary>
    ///     Builds an ARM request with a fresh delegated token.
    /// </summary>
    /// <param name="method">HTTP method used for the bounded provider request.</param>
    /// <param name="uri">Validated provider endpoint URI.</param>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <returns>The configured request with bounded provider headers and protected authorization.</returns>
    private static HttpRequestMessage Request(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
