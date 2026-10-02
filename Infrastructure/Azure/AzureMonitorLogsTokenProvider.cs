using System.Text.Json;
using Application.Abstractions.Azure;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Azure;

/// <summary>
///     Exchanges the connected user's encrypted refresh token for a short-lived Azure Monitor Logs token.
/// </summary>
public sealed class AzureMonitorLogsTokenProvider(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<AzureMonitorLogsOptions> options) : IAzureMonitorLogsTokenProvider
{
    /// <inheritdoc />
    public async Task<AzureMonitorLogsTokenResult> GetTokenAsync(Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return new AzureMonitorLogsTokenResult(null,
                "Azure Monitor Logs requires the deployment's connected user.");

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.TokenEndpoint) || string.IsNullOrWhiteSpace(settings.ClientId) ||
            string.IsNullOrWhiteSpace(settings.ClientSecret))
            return new AzureMonitorLogsTokenResult(null, "Azure Monitor Logs OAuth configuration is unavailable.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var remoteUser = await dbContext.Users.OfType<RemoteUser>()
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(remoteUser?.AzureRefreshToken))
            return new AzureMonitorLogsTokenResult(null,
                "Reconnect Azure to grant offline access for Azure Monitor Logs.");

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = remoteUser.AzureRefreshToken,
                ["scope"] = settings.Scope
            })
        };

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new AzureMonitorLogsTokenResult(null,
                "Azure Monitor Logs token exchange was denied. Reconnect Azure or verify consent.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var accessToken = payload.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
            return new AzureMonitorLogsTokenResult(null, "Azure did not return a Monitor Logs access token.");

        // Microsoft Entra may rotate refresh tokens. Persist the replacement through the existing encrypted column.
        if (payload.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement) &&
            !string.IsNullOrWhiteSpace(refreshTokenElement.GetString()))
        {
            remoteUser.AzureRefreshToken = refreshTokenElement.GetString();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new AzureMonitorLogsTokenResult(accessToken, null);
    }
}