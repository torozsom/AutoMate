using System.Reflection;
using Application.Abstractions.Docker;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Entities.Users;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Infrastructure.ApplicationServices.Orchestration.Lifecycle;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Infra.Orchestration;

/// <summary>
///     Docker startup reconciliation never changes remote Azure deployment status.
/// </summary>
public sealed class DeploymentCleanupHostedServiceTests
{
    /// <summary>
    ///     Reused Compose names never resurrect stopped history, including when the newest launch failed.
    /// </summary>
    /// <param name="latestStatus">Latest launch state that owns the Compose project name.</param>
    /// <returns>Completes after checking historical and latest status independently.</returns>
    [Theory]
    [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Stopped)]
    [InlineData(DeploymentStatus.Failed)]
    public async Task Startup_reconciles_latest_application_launch_without_rewriting_history(
        DeploymentStatus latestStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        await using var db = new ReconciliationContext(options, protector);
        await db.Database.EnsureCreatedAsync();
        var project = new CsProject
        {
            Name = "web",
            Path = "fixture",
            Application = new Domain.Entities.Projects.Application
            {
                Name = "fixture",
                SourceType = SourceType.Local,
                SourcePathOrUrl = "fixture",
                User = new LocalUser { Username = "fixture", Email = "fixture@example.test" }
            }
        };
        var historical = new Deployment { CsProject = project, Status = DeploymentStatus.Stopped };
        db.Deployments.Add(historical);
        await db.SaveChangesAsync();
        var historicalTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.Deployments.Where(d => d.Id == historical.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(d => d.CreatedAt, historicalTime));
        var latest = new Deployment { CsProject = project, Status = latestStatus };
        db.Deployments.Add(latest);
        await db.SaveChangesAsync();
        var proxy = DispatchProxy.Create(typeof(IDockerService), typeof(EmptyDocker));
        Assert.IsAssignableFrom<EmptyDocker>(proxy).Projects.Add("fixture");
        var docker = Assert.IsAssignableFrom<IDockerService>(proxy);
        await using (var operationDb = new ReconciliationContext(options, protector))
        {
            await new DeploymentReconciliation(operationDb, docker,
                NullLogger<DeploymentCleanupHostedService>.Instance).RunAsync(CancellationToken.None);
        }

        await using var check = new ReconciliationContext(options, protector);
        var states = await check.Deployments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Status);
        Assert.Equal(DeploymentStatus.Stopped, states[historical.Id]);
        Assert.Equal(latestStatus == DeploymentStatus.Failed ? DeploymentStatus.Failed : DeploymentStatus.Running,
            states[latest.Id]);
    }

    /// <summary>
    ///     Cloud launch, running and stopped history survive restart; genuinely local state still
    ///     reconciles.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Startup_cleanup_does_not_fail_or_stop_azure_deployments()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");

        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        await using var db = new ReconciliationContext(options, protector);

        await db.Database.EnsureCreatedAsync();
        var deployments = new List<Deployment>();

        foreach (var source in new[] { SourceType.Remote, SourceType.Local })
        foreach (var status in new[] { DeploymentStatus.Starting, DeploymentStatus.Running, DeploymentStatus.Stopped })
            deployments.Add(new Deployment
            {
                Status = status,
                CsProject = new CsProject
                {
                    Name = "web",
                    Path = "fixture",
                    Application = new Domain.Entities.Projects.Application
                    {
                        Name = "fixture",
                        SourceType = source,
                        SourcePathOrUrl = "fixture",
                        User = new LocalUser
                        {
                            Username = $"fixture-{source}-{status}",
                            Email = $"fixture-{source}-{status}@example.test"
                        }
                    }
                }
            });
        db.Deployments.AddRange(deployments);
        await db.SaveChangesAsync();
        var docker = DispatchProxy.Create<IDockerService, EmptyDocker>();

        await using (var operationDb = new ReconciliationContext(options, protector))
        {
            await new DeploymentReconciliation(operationDb, docker,
                NullLogger<DeploymentCleanupHostedService>.Instance).RunAsync(CancellationToken.None);
        }

        await using var check = new ReconciliationContext(options, protector);
        var states = await check.Deployments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Status);

        foreach (var cloud in deployments.Take(3)) Assert.Equal(cloud.Status, states[cloud.Id]);

        Assert.Equal(DeploymentStatus.Failed, states[deployments[3].Id]);
        Assert.Equal(DeploymentStatus.Stopped, states[deployments[4].Id]);
        Assert.Equal(DeploymentStatus.Stopped, states[deployments[5].Id]);
    }

    /// <summary>
    ///     Adapts chronological timestamp comparisons to the SQLite fixture's supported storage type.
    /// </summary>
    /// <param name="options">Isolated fixture database options.</param>
    /// <param name="protection">Ephemeral protection for fixture metadata.</param>
    private sealed class ReconciliationContext(
        DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider protection) : AutoMateDbContext(options, protection)
    {
        /// <summary>
        ///     Preserves chronological ordering using UTC ticks in SQLite.
        /// </summary>
        /// <param name="builder">Fixture model mappings.</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Deployment>().Property(e => e.CreatedAt)
                .HasConversion(e => e.UtcTicks, e => new DateTimeOffset(e, TimeSpan.Zero));
        }
    }

    /// <summary>
    ///     Represents an empty local Docker daemon; no Azure availability can be inferred from it.
    /// </summary>
    public class EmptyDocker : DispatchProxy
    {
        /// <summary>Fixture-owned running Compose inventory.</summary>
        public List<string> Projects { get; } = [];

        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="args">
        ///     Arguments for the selected fixture port or reflected component member; ignored by this
        ///     fixture.
        /// </param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        protected override object Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name == nameof(IDockerService.GetRunningProjectNamesAsync)
                ? Task.FromResult(Projects)
                : throw new NotSupportedException();
        }
    }
}
