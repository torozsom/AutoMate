using System.Net;
using FluentAssertions;
using Infrastructure.GitHub;
using Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.GitHub;

/// <summary>Checks safe remote-commit recovery and provider cooldown detection.</summary>
public sealed class GitHubSaasCommitRecoveryTests
{
    /// <summary>A retry finds a marked commit on the managed branch before attempting another push.</summary>
    [Fact]
    public async Task FindDeploymentCommitAsync_returns_the_prior_marked_sha()
    {
        var handler = new DelegateHttpMessageHandler(request =>
        {
            request.RequestUri!.Query.Should().Contain("sha=automate%2Fazure-deployment");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                                            [{"sha":"abc123","commit":{"message":"Add deployment\n\nAutoMate-Run: 0123456789abcdef"}}]
                                            """)
            };
        });
        var service = new GitHubService(new HttpClient(handler),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<GitHubService>.Instance);

        var sha = await service.FindDeploymentCommitAsync("installation-token", "acme", "web",
            "automate/azure-deployment", "AutoMate-Run: 0123456789abcdef");

        sha.Should().Be("abc123");
    }

    /// <summary>A GitHub reset header becomes a bounded installation cooldown.</summary>
    [Fact]
    public async Task FindDeploymentCommitAsync_respects_GitHub_rate_limit_reset()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(4).ToUnixTimeSeconds();
        var handler = new DelegateHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToString());
            return response;
        });
        var service = new GitHubService(new HttpClient(handler),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<GitHubService>.Instance);

        var call = async () => await service.FindDeploymentCommitAsync("installation-token", "acme", "web",
            "automate/azure-deployment", "AutoMate-Run: marker");

        var failure = await call.Should().ThrowAsync<GitHubRateLimitException>();
        failure.Which.RetryAt.ToUnixTimeSeconds().Should().Be(reset);
    }
}