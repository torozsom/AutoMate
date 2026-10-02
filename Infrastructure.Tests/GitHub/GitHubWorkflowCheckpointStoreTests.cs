using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.GitHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.GitHub;

/// <summary>Verifies durable GitHub Actions workflow and job checkpoints.</summary>
public sealed class GitHubWorkflowCheckpointStoreTests
{
    /// <summary>Ensures a new job checkpoint is persisted once and reused after reloading.</summary>
    [Fact]
    public async Task GetOrCreateJob_inserts_new_job_and_reuses_it_after_reload()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        var deploymentId = Guid.NewGuid();

        await using (var dbContext = new AutoMateDbContext(options, protector))
        {
            await dbContext.Database.EnsureCreatedAsync();
            dbContext.Deployments.Add(new Deployment
            {
                Id = deploymentId,
                CsProject = new CsProject
                {
                    Name = "Web",
                    Path = "Web/Web.csproj",
                    Application = new Domain.Entities.Application
                    {
                        Name = "Sample",
                        SourceType = SourceType.Remote,
                        SourcePathOrUrl = "https://github.com/example/sample",
                        User = new LocalUser { Username = "test", Email = "test@example.invalid" }
                    }
                }
            });
            await dbContext.SaveChangesAsync();

            var store = new GitHubWorkflowCheckpointStore(dbContext);
            var workflow = await store.GetOrCreateWorkflowAsync(deploymentId, 123, 1, CancellationToken.None);
            var job = await store.GetOrCreateJobAsync(workflow, 456, "deploy", CancellationToken.None);

            dbContext.Entry(job).State.Should().Be(EntityState.Unchanged);
            (await store.GetOrCreateJobAsync(workflow, 456, "deploy", CancellationToken.None)).Should().BeSameAs(job);
        }

        await using var verificationContext = new AutoMateDbContext(options, protector);
        var verificationStore = new GitHubWorkflowCheckpointStore(verificationContext);
        var reloadedWorkflow = await verificationStore.GetOrCreateWorkflowAsync(deploymentId, 123, 1,
            CancellationToken.None);
        var reloadedJob = await verificationStore.GetOrCreateJobAsync(reloadedWorkflow, 456, "deploy",
            CancellationToken.None);

        reloadedJob.JobId.Should().Be(456);
        (await verificationContext.Set<GitHubWorkflowJobCheckpoint>().CountAsync()).Should().Be(1);
    }
}