using System.IO.Compression;
using System.Net;
using Domain.Enums.GitHub;
using FluentAssertions;
using Infrastructure.GitHub.Api;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Test.Infra.TestSupport;

namespace Test.Infra.GitHub;

/// <summary>
///     Checks secure GitHub Actions job-log download and decoding behavior.
/// </summary>
public sealed class GitHubServiceWorkflowLogTests
{
    /// <summary>
    ///     Verifies that OAuth credentials are not sent to GitHub's signed log host.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Verifies that ZIP bytes are decoded before reaching the terminal pipeline.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Provides a cache-free GitHub service dependency for HTTP-focused tests.
    /// </summary>
    private sealed class NoOpDistributedCache : IDistributedCache
    {
        /// <summary>
        ///     Supplies a cache miss for every synchronous read.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        /// <returns>Null for every call in this fixture.</returns>
        public byte[]? Get(string key)
        {
            return null;
        }

        /// <summary>
        ///     Supplies a cache miss for every asynchronous read.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning null for every fixture call.</returns>
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            return Task.FromResult<byte[]?>(null);
        }

        /// <summary>
        ///     Performs no refresh work in this fixture.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        public void Refresh(string key)
        {
        }

        /// <summary>
        ///     Completes immediately without executing live refresh work.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Performs no remove work in this fixture.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        public void Remove(string key)
        {
        }

        /// <summary>
        ///     Completes immediately without executing live remove work.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Performs no set work in this fixture.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        /// <param name="value">Synthetic cached bytes; ignored by this fixture.</param>
        /// <param name="options">Cache-entry settings supplied to the fake; ignored by this fixture.</param>
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
        }

        /// <summary>
        ///     Completes immediately without executing live set work.
        /// </summary>
        /// <param name="key">Fixed connection-status query key; ignored by this fixture.</param>
        /// <param name="value">Synthetic cached bytes; ignored by this fixture.</param>
        /// <param name="options">Cache-entry settings supplied to the fake; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            return Task.CompletedTask;
        }
    }
}
