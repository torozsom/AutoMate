using System.Net.Http.Headers;
using System.Text.Json;

namespace Web.Configs;

/// <summary>
///     Resolves Azure subscription IDs for connected Azure accounts.
/// </summary>
internal static class AzureSubscriptionResolver
{
    /// <summary>
    ///     Azure subscriptions API version used by the account connection flow.
    /// </summary>
    private const string AzureSubscriptionsApiVersion = "2022-12-01";

    /// <summary>
    ///     Loads the first enabled Azure subscription ID, falling back to the first returned subscription.
    /// </summary>
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