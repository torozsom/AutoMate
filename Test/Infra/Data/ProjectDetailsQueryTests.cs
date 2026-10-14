using Application.Data.Apps;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Entities.Users;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Infrastructure.ApplicationServices.Data.Apps;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Test.Infra.Data;

/// <summary>
///     Exercises the real bounded project reads, complete-history aggregates and authorization in relational SQL.
/// </summary>
public sealed class ProjectDetailsQueryTests
{
    /// <summary>
    ///     Current metadata stays bounded while totals and six-row pages cover all owned historical deployments.
    /// </summary>
    /// <returns>Completes after querying both pages and inaccessible resources.</returns>
    [Fact]
    public async Task Current_graph_totals_pages_and_ownership_remain_independent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var clock = new Clock();
        await using var db = new QueryDb(options, clock);
        await db.Database.EnsureCreatedAsync();
        var app = new Domain.Entities.Projects.Application
        {
            Name = "fixture", SourcePathOrUrl = "fixture", SourceType = SourceType.Local,
            User = new LocalUser { Username = "owner", Email = "owner@example.invalid" },
            CsProjects =
            [
                new CsProject
                    { Name = "web", IsWebProject = true, Configuration = new Configuration { DotNetVersion = "10.0" } }
            ]
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        var first = clock.GetUtcNow();
        for (var index = 0; index < 14; index++)
        {
            clock.Now = first.AddMinutes(index);
            db.Deployments.Add(new Deployment
            {
                CsProjectId = app.CsProjects.Single().Id,
                Status = DeploymentStatus.Stopped,
                Outcome = (index % 3) switch
                {
                    0 => DeploymentOutcome.Succeeded, 1 => DeploymentOutcome.Failed, _ => DeploymentOutcome.Unknown
                }
            });
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
        var query = new ProjectDetailsQuery(db);
        var page = Assert.IsType<ProjectDetailsReadModel>(await query.ReadAsync(app.Id, app.UserId, 0));
        Assert.Equal(new ProjectDeploymentSummary(14, 5, 5, 4, first), page.Summary);
        Assert.Single(page.Project.CsProjects.Single().Deployments);
        Assert.Equal(6, page.History.Count);
        Assert.All(page.History, deployment => Assert.Equal("web", deployment.CsProject?.Name));
        Assert.Equal(first.AddMinutes(13), page.History[0].CreatedAt);
        var second = Assert.IsType<ProjectDetailsReadModel>(await query.ReadAsync(app.Id, app.UserId, 1));
        Assert.Equal(6, second.History.Count);
        Assert.All(second.History, deployment => Assert.Equal("web", deployment.CsProject?.Name));
        Assert.Empty(page.History.Select(d => d.Id).Intersect(second.History.Select(d => d.Id)));
        var third = Assert.IsType<ProjectDetailsReadModel>(await query.ReadAsync(app.Id, app.UserId, 2));
        Assert.Equal(2, third.History.Count);
        Assert.Null(await query.ReadAsync(app.Id, Guid.NewGuid(), 0));
        Assert.Null(await query.ReadAsync(Guid.NewGuid(), app.UserId, 0));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>
    ///     Uses sortable UTC storage for SQLite without changing the production PostgreSQL mapping.
    /// </summary>
    /// <param name="options">The isolated SQLite options.</param>
    /// <param name="clock">The deterministic persistence-time clock.</param>
    private sealed class QueryDb(DbContextOptions<AutoMateDbContext> options, TimeProvider clock)
        : AutoMateDbContext(options, new EphemeralDataProtectionProvider(), clock)
    {
        /// <summary>
        ///     Converts UTC timestamps to sortable SQLite integers for provider-independent query scenarios.
        /// </summary>
        /// <param name="builder">Builds the isolated test model.</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var property in builder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
                         .Where(property =>
                             property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
        }
    }

    /// <summary>
    ///     Advances recorded deployment time without real delays.
    /// </summary>
    private sealed class Clock : TimeProvider
    {
        /// <summary>The selected synthetic UTC instant.</summary>
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        ///     Returns the current synthetic time.
        /// </summary>
        /// <returns>The fixture UTC instant.</returns>
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }
}
