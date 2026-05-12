using Core.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Services.Data;

/// <summary>
///     The AutoMateDbContext class is responsible for managing the
///     database context for the AutoMate application. It defines the
///     DbSet properties and configures the relationships, encryption,
///     and constraints for the application entities.
/// </summary>
public class AutoMateDbContext(
    DbContextOptions<AutoMateDbContext> options,
    IDataProtectionProvider dataProtectionProvider) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>
    ///     Gets or sets the collection of User entities in the database.
    /// </summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>
    ///     Gets or sets the collection of Project entities in the database.
    /// </summary>
    public DbSet<Project> Projects => Set<Project>();

    /// <summary>
    ///     Gets or sets the collection of CsProject entities in the database.
    /// </summary>
    public DbSet<CsProject> CsProjects => Set<CsProject>();

    /// <summary>
    ///     Gets or sets the collection of ProjectConfiguration entities in the database.
    /// </summary>
    public DbSet<LocalProjectConfig> LocalProjectConfigs => Set<LocalProjectConfig>();

    /// <summary>
    ///     Gets or sets the collection of Deployment entities in the database.
    /// </summary>
    public DbSet<Deployment> Deployments => Set<Deployment>();

    /// <summary>
    ///     Gets or sets the collection of DataProtectionKey entities in the database.
    ///     Required for distributed data protection (e.g., across Docker containers).
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();


    /// <summary>
    ///     Configures the entity relationships, constraints, and data protection for the AutoMateDbContext.
    /// </summary>
    /// <param name="modelBuilder">The model builder that helps in the configuration.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Setup encryption for sensitive data (GitHub Tokens)
        var protector = dataProtectionProvider.CreateProtector("AutoMate.GitHubTokenProtector");
        var tokenConverter = new ValueConverter<string?, string?>(
            plainText => plainText != null ? protector.Protect(plainText) : null,
            encryptedText => encryptedText != null ? protector.Unprotect(encryptedText) : null
        );

        // Configure the User entity hierarchy (TPH)
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasDiscriminator<string>("user_type")
                .HasValue<GitHubUser>("github")
                .HasValue<LocalUser>("local");

            entity.HasIndex(u => u.Email).IsUnique();

            entity.Property(u => u.Email).HasMaxLength(255).IsRequired();
            entity.Property(u => u.Username).HasMaxLength(100).IsRequired();

            entity.HasMany(u => u.Projects)
                .WithOne(p => p.User)
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure the GitHubUser entity to encrypt the AccessToken property
        modelBuilder.Entity<GitHubUser>(entity =>
        {
            entity.Property(gu => gu.AccessToken).HasConversion(tokenConverter);
        });

        // Configure the Project entity
        modelBuilder.Entity<Project>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();

            entity.HasMany(p => p.CsProjects)
                .WithOne(csp => csp.Project)
                .HasForeignKey(csp => csp.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure the CsProject entity
        modelBuilder.Entity<CsProject>(entity =>
        {
            entity.HasOne(csp => csp.Configuration)
                .WithOne(c => c.CsProject)
                .HasForeignKey<LocalProjectConfig>(c => c.CsProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(csp => csp.Deployments)
                .WithOne(d => d.CsProject)
                .HasForeignKey(d => d.CsProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }


    /// <summary>
    ///     Overrides the SaveChangesAsync method to automatically set the CreatedAt and UpdatedAt properties.
    /// </summary>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateAuditFields();
        return base.SaveChangesAsync(cancellationToken);
    }


    /// <summary>
    ///     Overrides the synchronous SaveChanges method to ensure audit fields are updated even if called synchronously.
    /// </summary>
    /// <returns>The number of state entries written to the database.</returns>
    public override int SaveChanges()
    {
        UpdateAuditFields();
        return base.SaveChanges();
    }

    
    /// <summary>
    ///     Iterates through tracked entities and updates the CreatedAt and UpdatedAt timestamps.
    /// </summary>
    private void UpdateAuditFields()
    {
        var entries = ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified);

        var now = DateTimeOffset.UtcNow;

        foreach (var entry in entries)
        {
            entry.Entity.UpdatedAt = now;
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
            }
        }
    }
}