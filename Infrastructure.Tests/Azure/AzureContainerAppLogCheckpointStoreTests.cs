using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Azure;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Azure;

public sealed class AzureContainerAppLogCheckpointStoreTests
{
    [Fact]
    public async Task GetOrCreate_persists_one_cursor_per_deployment_and_source()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        Guid deploymentId;

        await using (var dbContext = new AutoMateDbContext(options, protector))
        {
            await dbContext.Database.EnsureCreatedAsync();
            deploymentId = Guid.NewGuid();
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
            dbContext.AzureContainerAppLogCheckpoints.Add(new AzureContainerAppLogCheckpoint
            {
                DeploymentId = deploymentId,
                Source = "console",
                LastTimestamp = DateTimeOffset.Parse("2026-09-28T10:00:00Z"),
                LastTieBreaker = "abc"
            });
            await dbContext.SaveChangesAsync();
        }

        await using (var dbContext = new AutoMateDbContext(options, protector))
        {
            var store = new AzureContainerAppLogCheckpointStore(dbContext);
            var checkpoint = await store.GetOrCreateAsync(deploymentId, "console", CancellationToken.None);
            var systemCheckpoint = await store.GetOrCreateAsync(deploymentId, "system", CancellationToken.None);

            checkpoint.LastTieBreaker.Should().Be("abc");
            systemCheckpoint.Source.Should().Be("system");
            await store.SaveAsync(CancellationToken.None);
        }

        await using var verificationContext = new AutoMateDbContext(options, protector);
        (await verificationContext.AzureContainerAppLogCheckpoints.CountAsync()).Should().Be(2);
    }
}