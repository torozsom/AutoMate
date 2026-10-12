using System.Reflection;
using Application.Abstractions.Docker;
using Application.Orchestration;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Orchestration;

/// <summary>
///     Docker startup reconciliation never changes remote Azure deployment status.
/// </summary>
public sealed class DeploymentCleanupHostedServiceTests
{
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
        await using var db = new AutoMateDbContext(options, protector);
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
                    Application = new Domain.Entities.Application
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
        await using var services = new ServiceCollection()
            .AddScoped<AutoMateDbContext>(_ => new AutoMateDbContext(options, protector))
            .AddSingleton(docker).BuildServiceProvider();
        var worker = new DeploymentCleanupHostedService(services, NullLogger<DeploymentCleanupHostedService>.Instance);
        await (Task)typeof(DeploymentCleanupHostedService).GetMethod("CleanupStuckDeploymentsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [CancellationToken.None])!;
        await using var check = new AutoMateDbContext(options, protector);
        var states = await check.Deployments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Status);
        foreach (var cloud in deployments.Take(3)) Assert.Equal(cloud.Status, states[cloud.Id]);
        Assert.Equal(DeploymentStatus.Failed, states[deployments[3].Id]);
        Assert.Equal(DeploymentStatus.Stopped, states[deployments[4].Id]);
    }

    /// <summary>
    ///     Represents an empty local Docker daemon; no Azure availability can be inferred from it.
    /// </summary>
    public class EmptyDocker : DispatchProxy
    {
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
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name == nameof(IDockerService.GetRunningProjectNamesAsync)
                ? Task.FromResult(new List<string>())
                : throw new NotSupportedException();
        }
    }
}