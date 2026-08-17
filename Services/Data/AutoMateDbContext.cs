using Core.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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
    ///     EF discriminator column used for the local and remote user hierarchy.
    /// </summary>
    private const string UserTypeDiscriminatorColumn = "user_type";

    /// <summary>
    ///     Discriminator value stored for GitHub-backed remote users.
    /// </summary>
    private const string RemoteUserDiscriminatorValue = "github";

    /// <summary>
    ///     Discriminator value stored for email/password local users.
    /// </summary>
    private const string LocalUserDiscriminatorValue = "local";

    /// <summary>
    ///     Data Protection purpose string for encrypted GitHub access tokens.
    /// </summary>
    private const string GitHubTokenProtectorPurpose = "AutoMate.GitHubTokenProtector";

    /// <summary>
    ///     Data Protection purpose string for encrypted Azure OAuth tokens.
    /// </summary>
    private const string AzureTokenProtectorPurpose = "AutoMate.AzureTokenProtector";

    /// <summary>
    ///     Maximum persisted length for user email addresses.
    /// </summary>
    private const int EmailMaxLength = 255;

    /// <summary>
    ///     Maximum persisted length for user display names.
    /// </summary>
    private const int UsernameMaxLength = 100;

    /// <summary>
    ///     Maximum persisted length for application names.
    /// </summary>
    private const int ApplicationNameMaxLength = 200;

    /// <summary>
    ///     Maximum persisted length for Azure account, tenant, and subscription identifiers.
    /// </summary>
    private const int AzureIdentifierMaxLength = 100;

    /// <summary>
    ///     Gets or sets the collection of User entities in the database.
    /// </summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>
    ///     Gets or sets the collection of Application entities in the database.
    /// </summary>
    public DbSet<Application> Applications => Set<Application>();

    /// <summary>
    ///     Gets or sets the collection of CsProject entities in the database.
    /// </summary>
    public DbSet<CsProject> CsProjects => Set<CsProject>();

    /// <summary>
    ///     Gets or sets the collection of ProjectConfiguration entities in the database.
    /// </summary>
    public DbSet<Configuration> AppConfigs => Set<Configuration>();

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

        ConfigureUserHierarchy(modelBuilder.Entity<User>());
        ConfigureRemoteUser(modelBuilder.Entity<RemoteUser>());
        ConfigureApplication(modelBuilder.Entity<Application>());
        ConfigureCsProject(modelBuilder.Entity<CsProject>());
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


    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        UpdateAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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


    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        UpdateAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
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
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = now;
        }
    }

    /// <summary>
    ///     Configures the shared user table, discriminator, indexes, and application relationship.
    /// </summary>
    private static void ConfigureUserHierarchy(EntityTypeBuilder<User> entity)
    {
        entity.HasDiscriminator<string>(UserTypeDiscriminatorColumn)
            .HasValue<RemoteUser>(RemoteUserDiscriminatorValue)
            .HasValue<LocalUser>(LocalUserDiscriminatorValue);

        entity.HasIndex(u => u.Email).IsUnique();

        entity.Property(u => u.Email).HasMaxLength(EmailMaxLength).IsRequired();
        entity.Property(u => u.Username).HasMaxLength(UsernameMaxLength).IsRequired();

        entity.HasMany(u => u.Applications)
            .WithOne(p => p.User)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    ///     Configures remote-user token encryption and Azure identifier constraints.
    /// </summary>
    private void ConfigureRemoteUser(EntityTypeBuilder<RemoteUser> entity)
    {
        var githubTokenConverter = CreateProtectedStringConverter(GitHubTokenProtectorPurpose);
        var azureTokenConverter = CreateProtectedStringConverter(AzureTokenProtectorPurpose);

        entity.Property(ru => ru.GitHubAccessToken).HasConversion(githubTokenConverter);
        entity.Property(ru => ru.AzureAccountId).HasMaxLength(AzureIdentifierMaxLength);
        entity.Property(ru => ru.AzureTenantId).HasMaxLength(AzureIdentifierMaxLength);
        entity.Property(ru => ru.AzureSubscriptionId).HasMaxLength(AzureIdentifierMaxLength);
        entity.Property(ru => ru.AzureAccessToken).HasConversion(azureTokenConverter);
        entity.Property(ru => ru.AzureRefreshToken).HasConversion(azureTokenConverter);
    }

    /// <summary>
    ///     Configures application naming constraints and child C# project ownership.
    /// </summary>
    private static void ConfigureApplication(EntityTypeBuilder<Application> entity)
    {
        entity.Property(a => a.Name).HasMaxLength(ApplicationNameMaxLength).IsRequired();

        entity.HasMany(a => a.CsProjects)
            .WithOne(csp => csp.Application)
            .HasForeignKey(csp => csp.AppId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    ///     Configures C# project configuration and deployment relationships.
    /// </summary>
    private static void ConfigureCsProject(EntityTypeBuilder<CsProject> entity)
    {
        entity.HasOne(csp => csp.Configuration)
            .WithOne(c => c.CsProject)
            .HasForeignKey<Configuration>(c => c.CsProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(csp => csp.Deployments)
            .WithOne(d => d.CsProject)
            .HasForeignKey(d => d.CsProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    ///     Creates an EF value converter that protects token values before persistence.
    /// </summary>
    private ValueConverter<string?, string?> CreateProtectedStringConverter(string purpose)
    {
        var protector = dataProtectionProvider.CreateProtector(purpose);

        return new ValueConverter<string?, string?>(
            plainText => plainText != null ? protector.Protect(plainText) : null,
            protectedText => protectedText != null ? protector.Unprotect(protectedText) : null);
    }
}