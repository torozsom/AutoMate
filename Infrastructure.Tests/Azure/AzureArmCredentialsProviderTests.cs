using System.Net;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Azure;
using Infrastructure.Data;
using Infrastructure.Tests.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Azure;

/// <summary>Verifies launches obtain current ARM credentials instead of reusing an expired login token.</summary>
public sealed class AzureArmCredentialsProviderTests
{
    /// <summary>Refresh token rotation is persisted; failures give a safe reconnect message.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Launch_refreshes_expired_credentials_and_handles_revocation(bool success)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AutoMateDbContext(new DbContextOptionsBuilder<AutoMateDbContext>()
            .UseSqlite(connection).Options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var user = new RemoteUser
        {
            Username = "test", Email = "test@example.invalid", AccountId = "1", AzureTenantId = "tenant",
            AzureSubscriptionId = "subscription", AzureAccessToken = "expired", AzureRefreshToken = "refresh",
            AzureTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(-2)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Should()
                .Contain("grant_type=refresh_token").And.Contain("refresh_token=refresh").And.NotContain("expired");
            return success
                ? DelegateHttpMessageHandler.Json(
                    "{\"access_token\":\"fresh\",\"refresh_token\":\"rotated\",\"expires_in\":3600}")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var provider = new AzureArmCredentialsProvider(db, new StubHttpClientFactory(handler),
            Options.Create(new AzureMonitorLogsOptions
                { TokenEndpoint = "https://example.invalid/token", ClientId = "client", ClientSecret = "secret" }));
        if (success)
        {
            var credentials = await provider.GetAsync(user.Id, CancellationToken.None);
            credentials.AccessToken.Should().Be("fresh");
            db.ChangeTracker.Clear();
            var saved = await db.Users.OfType<RemoteUser>().SingleAsync();
            saved.AzureRefreshToken.Should().Be("rotated");
            saved.AzureAccessToken.Should().Be("fresh");
            saved.AzureTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(55));
        }
        else
        {
            await provider.Invoking(p => p.GetAsync(user.Id, CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Reconnect Azure*");
        }
    }
}