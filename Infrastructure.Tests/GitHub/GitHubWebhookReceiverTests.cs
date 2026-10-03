using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.GitHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.GitHub;

/// <summary>Checks signed delivery acceptance, deduplication, and bounded metadata persistence.</summary>
public sealed class GitHubWebhookReceiverTests
{
    /// <summary>Only one verified receipt is retained for a redelivered GitHub event.</summary>
    [Fact]
    public async Task Receiver_verifies_signature_and_deduplicates_delivery_id()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new AutoMateDbContext(dbOptions, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var user = new RemoteUser { AccountId = "123", Email = "user@example.test", Username = "user" };
        var project = new Domain.Entities.Application
        {
            UserId = user.Id, Name = "web", SourceType = SourceType.Remote,
            SourcePathOrUrl = "https://github.com/acme/web"
        };
        db.Users.Add(user);
        db.Applications.Add(project);
        db.CloudDeploymentRuns.Add(new CloudDeploymentRun
        {
            UserId = user.Id, ProjectId = project.Id, IdempotencyKey = "key",
            InstallationId = 17, RepositoryId = 42, RepositoryOwner = "acme",
            RepositoryName = "web", BranchName = "automate/azure-deployment",
            EnvironmentName = "Production", WorkflowFileName = "deploy.yml",
            RegistryServer = "customer.azurecr.io", NextAttemptAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var receiver = new GitHubWebhookReceiver(db, Options.Create(new GitHubAppOptions
        {
            WebhookSecret = "test-signing-secret"
        }));
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "completed",
            installation = new { id = 17 },
            repository = new { id = 42 },
            workflow_run = new
            {
                id = 99, run_attempt = 1, head_sha = new string('a', 40),
                head_branch = "automate/azure-deployment",
                path = ".github/workflows/deploy.yml", status = "completed", conclusion = "success"
            }
        });
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("test-signing-secret"), body)).ToLowerInvariant();

        var invalid = async () => await receiver.ReceiveAsync(body, "sha256=00", "delivery-1",
            "workflow_run", CancellationToken.None);
        await invalid.Should().ThrowAsync<UnauthorizedAccessException>();
        await receiver.ReceiveAsync(body, signature, "delivery-1", "workflow_run", CancellationToken.None);
        await receiver.ReceiveAsync(body, signature, "delivery-1", "workflow_run", CancellationToken.None);

        db.CloudWebhookDeliveries.Should().ContainSingle().Which.WorkflowRunId.Should().Be(99);
        db.CloudWebhookDeliveries.Single().HeadSha.Should().Be(new string('a', 40));
        db.CloudWebhookDeliveries.Single().HeadBranch.Should().Be("automate/azure-deployment");
    }
}
