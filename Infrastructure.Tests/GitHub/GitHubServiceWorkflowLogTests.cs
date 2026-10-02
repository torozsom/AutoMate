using System.IO.Compression;
using System.Net;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.GitHub;
using Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.GitHub;

/// <summary>Checks secure GitHub Actions job-log download and decoding behavior.</summary>
public sealed class GitHubServiceWorkflowLogTests
{
    /// <summary>Verifies that OAuth credentials are not sent to GitHub's signed log host.</summary>
    [Fact]
    public async Task DownloadWorkflowJobLogsAsync_follows_the_signed_redirect_without_forwarding_the_access_token()
    {
        HttpRequestMessage? downloadRequest = null;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                request.Headers.Authorization!.Parameter.Should().Be("github-access-token");
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://logs.example.test/job-output") }
                };
            }

            downloadRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("first line\nsecond line")
            };
        });
        var service = new GitHubService(new HttpClient(handler), new NoOpDistributedCache(),
            NullLogger<GitHubService>.Instance);

        var result = await service.DownloadWorkflowJobLogsAsync("github-access-token", "owner", "repository", 42);

        result.Availability.Should().Be(GitHubWorkflowLogAvailability.Available);
        result.Content.Should().Be("first line\nsecond line");
        downloadRequest.Should().NotBeNull();
        downloadRequest!.Headers.Authorization.Should().BeNull();
    }

    /// <summary>Verifies that ZIP bytes are decoded before reaching the terminal pipeline.</summary>
    [Fact]
    public async Task DownloadWorkflowJobLogsAsync_decodes_the_zip_archive_before_streaming_to_the_terminal()
    {
        var handler = new DelegateHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                request.Headers.Authorization!.Parameter.Should().Be("github-access-token");
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://logs.example.test/job-output") }
                };
            }

            var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream,
                       ZipArchiveMode.Create,
                       true))
            {
                var entry = archive.CreateEntry("job.log");
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream);
                writer.Write("first line\nsecond line\n");
            }

            zipStream.Position = 0;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(zipStream)
            };
        });
        var service = new GitHubService(new HttpClient(handler), new NoOpDistributedCache(),
            NullLogger<GitHubService>.Instance);

        var result = await service.DownloadWorkflowJobLogsAsync("github-access-token", "owner", "repository", 42);

        result.Availability.Should().Be(GitHubWorkflowLogAvailability.Available);
        result.Content.Should().Contain("first line");
        result.Content.Should().Contain("second line");
        result.Content.Should().NotContain("PK");
    }

    /// <summary>Provides a cache-free GitHub service dependency for HTTP-focused tests.</summary>
    private sealed class NoOpDistributedCache : IDistributedCache
    {
        /// <inheritdoc />
        public byte[]? Get(string key)
        {
            return null;
        }

        /// <inheritdoc />
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            return Task.FromResult<byte[]?>(null);
        }

        /// <inheritdoc />
        public void Refresh(string key)
        {
        }

        /// <inheritdoc />
        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Remove(string key)
        {
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
        }

        /// <inheritdoc />
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            return Task.CompletedTask;
        }
    }
}