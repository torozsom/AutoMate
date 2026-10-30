using System.Net;
using System.Text.Json;
using Application.Abstractions.GitHub;
using Domain.DTO.Azure;
using Domain.DTO.GitHub;
using Infrastructure.GitHub.Api;
using Infrastructure.GitHub.Authentication;
using Infrastructure.GitHub.Secrets;
using Infrastructure.GitHub.Workflows;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Test.Infra.GitHub;

/// <summary>Protects provider wire names, optional workflow metadata and configuration binding.</summary>
public sealed class GitHubContractTests
{
    /// <summary>Provider JSON projects completed job steps and selects the requested workflow ahead of newer unrelated runs.</summary>
    [Fact]
    public void Workflow_wire_models_preserve_projection_and_selection()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var selected = new GitHubWorkflowRunItem(42, 0, "completed", "success", "https://example.test/run",
            "abc123", "main", ".github/workflows/deploy.yml", now);
        var wire = new GitHubWorkflowRunsResponse([
            selected, selected with
            {
                Id = 43, Path = ".github/workflows/other.yml", CreatedAt = now.AddMinutes(1)
            }
        ]);
        var restored = JsonSerializer.Deserialize<GitHubWorkflowRunsResponse>(JsonSerializer.Serialize(wire))!;
        var run = GitHubWorkflowRunMapper.SelectLatestMatchingRun(restored.WorkflowRuns, "deploy.yml", "abc123");
        Assert.NotNull(run);
        Assert.Equal(42, run.Id);
        Assert.Equal(1, run.Attempt);
        var jobs = new GitHubWorkflowJobsResponse([
            new GitHubWorkflowJobItem(7, "build", "completed", "success", "https://example.test/job", now,
                now.AddMinutes(2), [new GitHubWorkflowStepItem(1, "compile", "completed", "success")])
        ]);
        var restoredJobs = JsonSerializer.Deserialize<GitHubWorkflowJobsResponse>(JsonSerializer.Serialize(jobs))!;
        var job = Assert.Single(GitHubWorkflowJobMapper.Map(restoredJobs.Jobs));
        Assert.Equal(7, job.Id);
        Assert.Equal(now.AddMinutes(2), job.CompletedAt);
        Assert.Equal("compile", Assert.Single(job.Steps).Name);
    }

    /// <summary>The Application workflow-dispatch port sends the selected branch with the required GitHub route and payload.</summary>
    [Fact]
    public async Task Workflow_dispatch_preserves_branch_and_endpoint()
    {
        using var handler = new WorkflowDispatchHandler();
        using var client = new HttpClient(handler, false);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        IGitHubService service = new GitHubService(client, cache,
            NullLogger<GitHubService>.Instance);
        await service.DispatchWorkflowAsync("synthetic-token", "owner", "repository", "deploy.yml", "feature/fixture");
        var request = handler.Request ?? throw new InvalidOperationException("No dispatch request was sent.");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/owner/repository/actions/workflows/deploy.yml/dispatches", request.Path);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("feature/fixture", json.RootElement.GetProperty("ref").GetString());
    }

    /// <summary>Repository secret requests preserve GitHub's required snake-case payload fields.</summary>
    [Fact]
    public void Secret_and_dispatch_payloads_preserve_wire_names()
    {
        var secret = new GitHubRepositorySecretRequest("synthetic-ciphertext", "key-1");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(secret));
        Assert.Equal(secret.EncryptedValue, json.RootElement.GetProperty("encrypted_value").GetString());
        Assert.Equal(secret.KeyId, json.RootElement.GetProperty("key_id").GetString());
        Assert.False(json.RootElement.TryGetProperty("EncryptedValue", out _));

        var dispatch = new GitHubWorkflowDispatchRequest("refs/heads/main");
        using var dispatchJson = JsonDocument.Parse(JsonSerializer.Serialize(dispatch));
        Assert.Equal(dispatch.Ref, dispatchJson.RootElement.GetProperty("ref").GetString());
    }

    /// <summary>Workflow branch, SHA and job timestamps survive DTO serialization without precision loss.</summary>
    [Fact]
    public void Workflow_projections_preserve_commit_and_job_timing()
    {
        var run = JsonSerializer.Deserialize<GitHubWorkflowRunDto>(
            JsonSerializer.Serialize(new GitHubWorkflowRunDto { HeadSha = "abc123", HeadBranch = "main" }))!;
        Assert.Equal("abc123", run.HeadSha);
        Assert.Equal("main", run.HeadBranch);
        var started = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var job = JsonSerializer.Deserialize<GitHubWorkflowJobDto>(JsonSerializer.Serialize(
            new GitHubWorkflowJobDto { StartedAt = started, CompletedAt = started.AddMinutes(2) }))!;
        Assert.Equal(started, job.StartedAt);
        Assert.Equal(started.AddMinutes(2), job.CompletedAt);
    }

    /// <summary>Private repository metadata, federation issuer and numeric App IDs retain their contracts.</summary>
    [Fact]
    public void Provider_metadata_and_app_configuration_preserve_values()
    {
        var repository = new GitHubRepositoryDto { IsPrivate = true };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(repository));
        Assert.True(json.RootElement.GetProperty("private").GetBoolean());
        var federation = new AzureOidcSetupResultDto { Issuer = "https://token.actions.githubusercontent.com" };
        using var federationJson = JsonDocument.Parse(JsonSerializer.Serialize(federation));
        Assert.Equal(federation.Issuer, federationJson.RootElement.GetProperty("Issuer").GetString());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["GitHubApp:AppId"] = "123456" }).Build();
        var options = new GitHubAppOptions { AppId = 1 };
        configuration.GetSection("GitHubApp").Bind(options);
        Assert.Equal(123456, options.AppId);
    }

    /// <summary>Captures a dispatch request before its HTTP message/content are disposed.</summary>
    private sealed class WorkflowDispatchHandler : HttpMessageHandler
    {
        /// <summary>Detached method, route and payload observed at the transport boundary.</summary>
        public (HttpMethod Method, string Path, string Body)? Request { get; private set; }

        /// <summary>Copies the cancellation-aware payload and simulates GitHub accepting the dispatch.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = (request.Method, request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
