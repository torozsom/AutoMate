using System.Text.Json;
using Domain.DTO;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Azure;

/// <summary>
///     Refreshes Azure Resource Manager credentials when a cloud launch starts.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
public sealed class AzureArmCredentialsProvider(
    AutoMateDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    IOptions<AzureMonitorLogsOptions> options)
{
    /// <summary>
    ///     Returns a current ARM token while keeping refresh tokens in the protected user row.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning current scoped ARM authorization metadata.</returns>
    /// <exception cref="InvalidOperationException">Reconnect Azure before deploying.</exception>
    public async Task<AzureCloudCredentialsDto> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.OfType<RemoteUser>()
                       .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
                   ?? throw new InvalidOperationException("Reconnect Azure before deploying.");
        if (string.IsNullOrWhiteSpace(user.AzureRefreshToken) ||
            string.IsNullOrWhiteSpace(user.AzureTenantId) ||
            string.IsNullOrWhiteSpace(user.AzureSubscriptionId))
            throw new InvalidOperationException("Reconnect Azure to grant offline access before deploying.");
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = user.AzureRefreshToken,
                ["scope"] = "https://management.azure.com/.default"
            })
        };
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Azure authorization expired. Reconnect Azure before deploying.");
        using var payload = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var token = payload.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("Azure did not issue an ARM token.");
        if (payload.RootElement.TryGetProperty("refresh_token", out var rotated) &&
            !string.IsNullOrWhiteSpace(rotated.GetString()))
            user.AzureRefreshToken = rotated.GetString();
        user.AzureAccessToken = token;
        if (payload.RootElement.TryGetProperty("expires_in", out var lifetime) && lifetime.TryGetInt32(out var seconds))
            user.AzureTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AzureCloudCredentialsDto
        {
            TenantId = user.AzureTenantId,
            SubscriptionId = user.AzureSubscriptionId,
            AccessToken = token
        };
    }
}
