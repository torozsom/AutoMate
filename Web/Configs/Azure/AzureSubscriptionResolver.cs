using System.Net.Http.Headers;
using System.Text.Json;

namespace Web.Configs.Azure;

/// <summary>
///     Resolves Azure subscription IDs for connected Azure accounts.
/// </summary>
internal static class AzureSubscriptionResolver
{
    /// <summary>Azure subscriptions API version used by the account connection flow.</summary>
    private const string AzureSubscriptionsApiVersion = "2022-12-01";

    /// <summary>
    ///     Loads the first enabled Azure subscription ID, falling back to the first returned
    ///     subscription.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="httpClientFactory">Creates the scoped Azure HTTP client.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>
    ///     A task returning the first enabled subscription or first available fallback, or null when no
    ///     subscription can be selected.
    /// </returns>
    /// <exception cref="HttpRequestException">The Azure subscription endpoint cannot be reached.</exception>
    /// <exception cref="System.Text.Json.JsonException">The subscription response is malformed JSON.</exception>
    /// <exception cref="OperationCanceledException">The request is canceled or reaches its HTTP timeout.</exception>
    /// <exception cref="InvalidOperationException">Subscription fields have incompatible JSON kinds.</exception>
    public static async Task<string?> GetDefaultSubscriptionIdAsync(string? accessToken,
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
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!payload.RootElement.TryGetProperty("value", out var subscriptionsElement) ||
            subscriptionsElement.ValueKind != JsonValueKind.Array)
            return null;

        return GetPreferredSubscriptionId(subscriptionsElement);
    }

    /// <summary>
    ///     Selects the first enabled subscription or the first available subscription as a fallback.
    /// </summary>
    /// <param name="subscriptionsElement">Azure subscriptions response array.</param>
    /// <returns>The first enabled subscription ID, the first available fallback, or null when none is present.</returns>
    /// <exception cref="InvalidOperationException">The subscription response has incompatible object or string field kinds.</exception>
    private static string? GetPreferredSubscriptionId(JsonElement subscriptionsElement)
    {
        string? firstFallback = null;

        foreach (var subscription in subscriptionsElement.EnumerateArray())
        {
            if (!subscription.TryGetProperty("subscriptionId", out var idElement))
                continue;

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            firstFallback ??= id;

            var state = subscription.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString()
                : null;

            if (string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase))
                return id;
        }

        return firstFallback;
    }
}
