using System.Net;
using Domain.Entities;
using Infrastructure.Azure;
using Infrastructure.Data;
using Infrastructure.Tests.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Azure;

/// <summary>
///     Verifies fixed consent guidance without exposing provider response messages.
/// </summary>
public sealed class AzureMonitorLogsTokenProviderTests
{
    /// <summary>
    ///     Only the recognized consent error selects permission guidance; arbitrary messages stay
    ///     private.
    /// </summary>
    /// <param name="body">The original webhook body used for signature validation.</param>
    /// <param name="consent">Legacy preference value used to verify automatic collection behavior.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("{\"error_codes\":[65001],\"error_description\":\"private-data\"}", true)]
    [InlineData("{\"error_codes\":[70000],\"error_description\":\"private-data\"}", false)]
    [InlineData("private-data", false)]
    public async Task Denied_token_returns_safe_guidance(string body, bool consent)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        await using var db = new AutoMateDbContext(options, protector);
        await db.Database.EnsureCreatedAsync();
        var user = new RemoteUser
        {
            AccountId = "fixture",
            Username = "fixture",
            Email = "fixture@example.test",
            AzureRefreshToken = "refresh"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var services = new ServiceCollection().AddScoped(_ => new AutoMateDbContext(options, protector))
            .BuildServiceProvider();
        var factory = new StubHttpClientFactory(new DelegateHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) }));
        var provider = new AzureMonitorLogsTokenProvider(services.GetRequiredService<IServiceScopeFactory>(), factory,
            Options.Create(new AzureMonitorLogsOptions
            {
                ClientId = "fixture",
                ClientSecret = "fixture",
                TokenEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token"
            }));
        var result = await provider.GetTokenAsync(user.Id);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("private-data", result.FailureReason);
        Assert.Equal(consent, result.FailureReason!.Contains("Data.Read", StringComparison.Ordinal));
    }
}