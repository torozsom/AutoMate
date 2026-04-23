using System.Text.RegularExpressions;
using Core.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Services.Data;

/// <summary>
///     The AutoMateDbContext class is responsible for managing the
///     database context for the AutoMate application. It defines the
///     DbSet properties for the User, Project, ProjectConfiguration,
///     and Deployment entities, and configures the relationships between
///     these entities using the OnModelCreating method.
/// </summary>
/// <param name="options">The options to pass to the base class.</param>
public partial class AutoMateDbContext(
    DbContextOptions<AutoMateDbContext> options,
    IDataProtectionProvider dataProtectionProvider) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>
    ///     Gets or sets the collection of User entities in the database.
    /// </summary>
    public DbSet<User> Users { get; set; }

    /// <summary>
    ///     Gets or sets the collection of Project entities in the database.
    /// </summary>
    public DbSet<Project> Projects { get; set; }

    /// <summary>
    ///     Gets or sets the collection of CsProject entities in the database.
    /// </summary>
    public DbSet<CsProject> CsProjects { get; set; }

    /// <summary>
    ///     Gets or sets the collection of ProjectConfiguration entities in the database.
    /// </summary>
    public DbSet<LocalProjectConfig> LocalProjectConfigs { get; set; }

    /// <summary>
    ///     Gets or sets the collection of Deployment entities in the database.
    /// </summary>
    public DbSet<Deployment> Deployments { get; set; }

    /// <summary>
    ///     Gets or sets the collection of DataProtectionKey entities in the database.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }


    /// <summary>
    ///     Configures the entity relationships and constraints for the AutoMateDbContext.
    /// </summary>
    /// <param name="modelBuilder">The model builder that helps in the configuration.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ApplySnakeCaseNaming(modelBuilder);

        var protector = dataProtectionProvider.CreateProtector("AutoMate.GitHubTokenProtector");

        var tokenConverter = new ValueConverter<string?, string?>(
            plainText => plainText != null ? protector.Protect(plainText) : null,
            encryptedText => encryptedText != null ? protector.Unprotect(encryptedText) : null
        );


        // Configure the User entity
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
    /// <param name="cancellationToken">The</param>
    /// <returns></returns>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified);

        var now = DateTimeOffset.UtcNow;

        foreach (var entry in entries)
        {
            entry.Entity.UpdatedAt = now;
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAt = now;
        }

        return base.SaveChangesAsync(cancellationToken);
    }


    /// <summary>
    ///     A private method that applies snake_case naming conventions to the database schema by
    ///     iterating through the entity types, properties, keys, foreign keys, and indexes in the
    ///     model builder and converting their names to snake_case using the ToSnakeCase method.
    /// </summary>
    /// <param name="modelBuilder"></param>
    private static void ApplySnakeCaseNaming(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName != null)
                entity.SetTableName(ToSnakeCase(tableName));

            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));

            foreach (var key in entity.GetKeys())
                key.SetName(ToSnakeCase(key.GetName()));

            foreach (var key in entity.GetForeignKeys())
                key.SetConstraintName(ToSnakeCase(key.GetConstraintName()));

            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()));
        }
    }


    /// <summary>
    ///     A static method that defines a regular expression to match the pattern of a lowercase letter
    ///     or digit followed by an uppercase letter, used for converting PascalCase to snake_case.
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex MyRegex();


    /// <summary>
    ///     A private static method that converts a given string from PascalCase to snake_case using a regular expression.
    /// </summary>
    /// <param name="input">The input PascalCase string to be converted.</param>
    /// <returns>The snake_case variant of the input.</returns>
    private static string ToSnakeCase(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;
        return MyRegex().Replace(input, "$1_$2").ToLowerInvariant();
    }
}