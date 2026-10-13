using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Test.Infra.Diagnostics;

/// <summary>
///     Verifies retained diagnostic replay and rejection of new legacy payload writes.
/// </summary>
public sealed class DeploymentDiagnosticReplayTests
{
    /// <summary>
    ///     Even direct legacy-store calls cannot insert new diagnostic payloads.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Legacy_store_rejects_new_payloads_before_database_access()
    {
        await using var db = new AutoMateDbContext(
            new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite("Data Source=:memory:").Options,
            new EphemeralDataProtectionProvider());
        var store = new DeploymentDiagnosticStore(db, new DiagnosticRedactor());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistAsync(Event(Guid.NewGuid(), Guid.NewGuid(), "password=private-value"), "build"));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>
    ///     Legacy replay preserves bounded ordering, ownership and expiry.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Recent_history_is_ordered_bounded_project_scoped_and_excludes_expired_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new SqliteReplayDbContext(options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var firstProject = Guid.NewGuid();
        var secondProject = Guid.NewGuid();
        var firstDeployment = Guid.NewGuid();
        var secondDeployment = Guid.NewGuid();
        db.Deployments.Add(CreateDeployment(firstProject, firstDeployment));
        db.Deployments.Add(CreateDeployment(secondProject, secondDeployment));
        await db.SaveChangesAsync();

        var store = new DeploymentDiagnosticStore(db, new DiagnosticRedactor());
        var safeMessage = new DiagnosticRedactor().Redact(Event(firstProject, firstDeployment, "password=secret"))
            .Event.Message;
        db.DeploymentDiagnosticRecords.AddRange(
            Record(1, firstProject, firstDeployment, "first", DateTimeOffset.UtcNow.AddMinutes(-1)),
            Record(2, firstProject, firstDeployment, safeMessage),
            Record(3, secondProject, secondDeployment, "other"));
        await db.SaveChangesAsync();

        var history = await store.ReadRecentAsync(firstProject, firstDeployment, 1);
        history.Events.Should().ContainSingle().Which.OrderId.Should().Be(2);
        history.Events[0].Message.Should().Be("password=[REDACTED]");
        history.EarlierOmitted.Should().BeFalse();
        (await store.DeleteExpiredAsync(100)).Should().Be(1);
        db.DeploymentDiagnosticRecords.Count().Should().Be(2);
    }

    /// <summary>
    ///     Legacy replay and context mask supported secrets without rewriting stored rows.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Replay_reports_when_older_messages_are_omitted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new SqliteReplayDbContext(options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var projectId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        db.Deployments.Add(CreateDeployment(projectId, deploymentId));
        await db.SaveChangesAsync();
        var store = new DeploymentDiagnosticStore(db, new DiagnosticRedactor());
        db.DeploymentDiagnosticRecords.AddRange(
            Record(1, projectId, deploymentId, "first"),
            Record(2, projectId, deploymentId, "password=private-value"),
            Record(3, projectId, deploymentId, "third"));
        await db.SaveChangesAsync();

        var history = await store.ReadRecentAsync(projectId, deploymentId, 2);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(history));
        Assert.Contains("private-value",
            (await db.DeploymentDiagnosticRecords.SingleAsync(row => row.OrderId == 2)).Message);
        Assert.DoesNotContain("private-value",
            JsonSerializer.Serialize(await store.ReadAfterAsync(projectId, deploymentId, 1, 10)));
        Assert.DoesNotContain("private-value", await store.BuildContextAsync(deploymentId, 4096));
        history.EarlierOmitted.Should().BeTrue();
        history.Events.Select(item => item.Message).Should().Equal("password=[REDACTED]", "third");
    }

    /// <summary>
    ///     Legacy uncorrelated output remains confined to its project and deployment lifetime.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Replay_recovers_legacy_uncorrelated_output_only_within_its_project_and_deployment_window()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new SqliteReplayDbContext(options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var projectId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstDeployment = CreateDeployment(projectId, firstId);
        db.Deployments.Add(firstDeployment);
        await db.SaveChangesAsync();
        db.Deployments.Add(new Deployment { Id = secondId, CsProjectId = firstDeployment.CsProjectId });
        await db.SaveChangesAsync();
        var first = await db.Deployments.FindAsync(firstId);
        var second = await db.Deployments.FindAsync(secondId);
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        first!.CreatedAt = start;
        second!.CreatedAt = start.AddHours(1);
        await db.SaveChangesAsync();

        db.DeploymentDiagnosticRecords.AddRange(
            new DeploymentDiagnosticRecord
            {
                OrderId = 1,
                ProjectId = projectId,
                TimestampUtc = start.AddMinutes(30),
                Source = "GitHubActions",
                Kind = "Log",
                Severity = "Information",
                Message = "first cloud",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            },
            new DeploymentDiagnosticRecord
            {
                OrderId = 2,
                ProjectId = projectId,
                TimestampUtc = start.AddHours(1).AddMinutes(1),
                Source = "GitHubActions",
                Kind = "Log",
                Severity = "Information",
                Message = "second cloud",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            });
        await db.SaveChangesAsync();

        var store = new DeploymentDiagnosticStore(db, new DiagnosticRedactor());
        var firstHistory = await store.ReadRecentAsync(projectId, firstId, 10);
        firstHistory.Events.Select(item => item.Message).Should().Equal("first cloud");
        firstHistory.Events[0].TerminalChannel.Should().Be("github-actions");
        (await store.ReadRecentAsync(projectId, secondId, 10)).Events.Select(item => item.Message)
            .Should().Equal("second cloud");
        (await store.ReadRecentAsync(Guid.NewGuid(), firstId, 10)).Events.Should().BeEmpty();
    }

    /// <summary>
    ///     Creates event data with the supplied identities and synthetic scenario values.
    /// </summary>
    /// <param name="projectId">Synthetic project identity.</param>
    /// <param name="deploymentId">Synthetic deployment identity.</param>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <returns>The configured deployment diagnostic event instance or response for this scenario.</returns>
    private static DeploymentDiagnosticEvent Event(Guid projectId, Guid deploymentId, string message)
    {
        return new DeploymentDiagnosticEvent(projectId, deploymentId, DeploymentDiagnosticSource.DockerCompose,
            DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information,
            DateTimeOffset.UtcNow, message, new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build));
    }

    /// <summary>
    ///     Creates record data with the supplied identities and synthetic scenario values.
    /// </summary>
    /// <param name="orderId">The durable ordering position; negative values represent unsaved live observations.</param>
    /// <param name="projectId">Synthetic project identity.</param>
    /// <param name="deploymentId">Synthetic deployment identity.</param>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <param name="expiresAt">The expiry time of the token or stored envelope, if known.</param>
    /// <returns>The configured deployment diagnostic record instance or response for this scenario.</returns>
    private static DeploymentDiagnosticRecord Record(long orderId, Guid projectId, Guid deploymentId,
        string message, DateTimeOffset? expiresAt = null)
    {
        return new DeploymentDiagnosticRecord
        {
            OrderId = orderId,
            ProjectId = projectId,
            DeploymentId = deploymentId,
            TimestampUtc = DateTimeOffset.UtcNow,
            Source = "DockerCompose",
            Kind = "Log",
            Severity = "Information",
            Message = message,
            TerminalChannel = "build",
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(30)
        };
    }

    /// <summary>
    ///     Creates create deployment data with the supplied identities and synthetic scenario values.
    /// </summary>
    /// <param name="projectId">Synthetic project identity.</param>
    /// <param name="deploymentId">Synthetic deployment identity.</param>
    /// <returns>The configured deployment instance or response for this scenario.</returns>
    private static Deployment CreateDeployment(Guid projectId, Guid deploymentId)
    {
        return new Deployment
        {
            Id = deploymentId,
            CsProject = new CsProject
            {
                Name = "Web",
                Path = "Web/Web.csproj",
                Application = new Domain.Entities.Application
                {
                    Id = projectId,
                    Name = "Sample",
                    SourceType = SourceType.Local,
                    SourcePathOrUrl = "C:/sample",
                    User = new LocalUser { Username = "test", Email = $"{projectId:N}@example.invalid" }
                }
            }
        };
    }

    /// <summary>
    ///     Converts diagnostic timestamps for ordered SQLite fixture reads.
    /// </summary>
    /// <param name="options">EF options for the fixture metadata database.</param>
    /// <param name="dataProtectionProvider">Verifies time-limited project subscription tokens.</param>
    private sealed class SqliteReplayDbContext(
        DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider dataProtectionProvider)
        : AutoMateDbContext(options, dataProtectionProvider)
    {
        /// <summary>
        ///     Adapts timestamp storage and metadata mappings for the SQLite fixture model.
        /// </summary>
        /// <param name="modelBuilder">EF builder receiving the fixture model mappings.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<DeploymentDiagnosticRecord>().Property(item => item.ExpiresAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<DeploymentDiagnosticRecord>().Property(item => item.TimestampUtc)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<Deployment>().Property(item => item.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }
    }
}