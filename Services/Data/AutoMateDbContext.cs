using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Services.Data;

/// <summary>
///     The AutoMateDbContext class is responsible for managing the
///     database context for the AutoMate application. It defines the
///     DbSet properties for the User, Project, ProjectConfiguration,
///     and Deployment entities, and configures the relationships between
///     these entities using the OnModelCreating method.
/// </summary>
/// <param name="options">The options to pass to the base class.</param>
public class AutoMateDbContext(DbContextOptions<AutoMateDbContext> options) : DbContext(options)
{
    /// The Users DbSet represents the collection of User entities in the database.
    public DbSet<User> Users { get; set; }

    /// The Projects DbSet represents the collection of Project entities in the database.
    public DbSet<Project> Projects { get; set; }

    /// The ProjectConfigurations DbSet represents the collection of ProjectConfiguration entities in the database.
    public DbSet<ProjectConfiguration> ProjectConfigurations { get; set; }

    /// The Deployments DbSet represents the collection of Deployment entities in the database.
    public DbSet<Deployment> Deployments { get; set; }


    /// <summary>
    ///     Configures the entity relationships and constraints for the AutoMateDbContext.
    /// </summary>
    /// <param name="modelBuilder">The model builder that helps in the configuration.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .HasMany(u => u.Projects)
            .WithOne(p => p.User)
            .HasForeignKey(p => p.UserId);

        modelBuilder.Entity<Project>()
            .HasOne(p => p.Configuration)
            .WithOne(pc => pc.Project)
            .HasForeignKey<ProjectConfiguration>(pc => pc.ProjectId);

        modelBuilder.Entity<Project>()
            .HasMany(p => p.Deployments)
            .WithOne(d => d.Project)
            .HasForeignKey(d => d.ProjectId);
    }
}