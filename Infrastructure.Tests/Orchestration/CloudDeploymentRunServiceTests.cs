using Application.Abstractions.GitHub;
using Application.Orchestration;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.ApplicationServices.Orchestration;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Orchestration;

/// <summary>Verifies SaaS admission authorization, idempotency, quota, and encrypted snapshots.</summary>
public sealed class CloudDeploymentRunServiceTests
{
    /// <summary>Repeated submissions share one durable run and never persist a raw token.</summary>
    [Fact]
    public async Task Start_is_idempotent_and_stores_a_protected_token_free_snapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service();
        var start = fixture.Start("retry-key");
        start.Config.CustomEnvVars["PASSWORD"] = "very-secret-value";

        var first = await service.StartAsync(start);
        var second = await service.StartAsync(start);

        second.RunId.Should().Be(first.RunId);
        fixture.Db.CloudDeploymentRuns.Should().ContainSingle();
        fixture.Db.CloudRunOutbox.Should().ContainSingle();
        fixture.Db.CloudDeploymentRuns.Single().Phase.Should().Be(CloudRunPhase.Queued);
        fixture.Db.CloudDeploymentRuns.Single().SnapshotJson.Should().Contain("very-secret-value");
        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT SnapshotJson FROM CloudDeploymentRuns";
        var persisted = (string?)await command.ExecuteScalarAsync();
        persisted.Should().NotContain("very-secret-value").And.NotContain("oauth-user-token");
    }

    /// <summary>One account cannot fill another account's queue or submit to its project.</summary>
    [Fact]
    public async Task Admission_enforces_owner_and_per_user_queue_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service().StartAsync(fixture.Start("first"));
        var second = fixture.Start("second");
        var action = async () => await fixture.Service().StartAsync(second);
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*queue is full*");

        var foreign = fixture.Start("foreign") with { UserId = Guid.NewGuid() };
        var denied = async () => await fixture.Service().StartAsync(foreign);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    /// <summary>Isolated SQLite fixture uses the production EF model and a shared data protector.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>Open in-memory connection used by all fixture queries.</summary>
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");

        /// <summary>Scoped EF context under test.</summary>
        public AutoMateDbContext Db { get; private set; } = null!;

        /// <summary>Authorized account.</summary>
        private Guid UserId { get; } = Guid.NewGuid();

        /// <summary>Owned remote project.</summary>
        private Guid ProjectId { get; } = Guid.NewGuid();

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }

        /// <summary>Creates a schema and a user-owned remote project.</summary>
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoMateDbContext>()
                .UseSqlite(fixture.Connection).Options;
            fixture.Db = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
            await fixture.Db.Database.EnsureCreatedAsync();
            fixture.Db.Users.Add(new RemoteUser
            {
                Id = fixture.UserId, AccountId = "123", Email = "user@example.test", Username = "user",
                AzureRefreshToken = "refresh", AzureTenantId = "tenant", AzureSubscriptionId = "subscription"
            });
            fixture.Db.Applications.Add(new Domain.Entities.Application
            {
                Id = fixture.ProjectId, UserId = fixture.UserId, Name = "web",
                SourceType = SourceType.Remote, SourcePathOrUrl = "https://github.com/acme/web"
            });
            await fixture.Db.SaveChangesAsync();
            return fixture;
        }

        /// <summary>Builds a service with a one-item queue for quota verification.</summary>
        public CloudDeploymentRunService Service()
        {
            return new CloudDeploymentRunService(Db, new StubGitHubApp(),
                Options.Create(new CloudSaasOptions { MaxQueuedPerUser = 1 }));
        }

        /// <summary>Builds a valid request for the owned project.</summary>
        public CloudDeploymentStart Start(string key)
        {
            return new CloudDeploymentStart(UserId, ProjectId, key, "acme", "web",
                "oauth-user-token", new DeploymentConfigDto
                {
                    ProjectId = ProjectId, CloudRegistryName = "customer.azurecr.io",
                    EnvironmentName = "Production"
                }, new ProjectMetadataDto(), "web", ".");
        }
    }

    /// <summary>Offline GitHub App authorization stub.</summary>
    private sealed class StubGitHubApp : IGitHubAppCredentials
    {
        /// <inheritdoc />
        public Task<(long InstallationId, long RepositoryId)> ResolveRepositoryAsync(string userAccessToken,
            string owner, string repository, CancellationToken cancellationToken)
        {
            return Task.FromResult((17L, 42L));
        }

        /// <inheritdoc />
        public Task<string> CreateInstallationTokenAsync(long installationId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult("installation-token");
        }
    }
}