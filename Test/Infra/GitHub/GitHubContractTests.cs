using System.Text.Json;
using Domain.DTO.Azure;
using Domain.DTO.GitHub;
using Infrastructure.GitHub.Authentication;
using Infrastructure.GitHub.Secrets;
using Microsoft.Extensions.Configuration;

namespace Test.Infra.GitHub;

/// <summary>Protects provider wire names, optional workflow metadata and configuration binding.</summary>
public sealed class GitHubContractTests
{
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
}
