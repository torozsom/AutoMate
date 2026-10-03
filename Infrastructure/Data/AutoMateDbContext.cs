using Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Data;

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

    /// <summary>Separates protected SaaS request snapshots from OAuth token protection.</summary>
    private const string CloudRunSnapshotProtectorPurpose = "AutoMate.CloudRunSnapshot";

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
    public DbSet<Domain.Entities.Application> Applications => Set<Domain.Entities.Application>();

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

    /// <summary>Durable SaaS cloud deployment requests.</summary>
    public DbSet<CloudDeploymentRun> CloudDeploymentRuns => Set<CloudDeploymentRun>();

    /// <summary>Transactional launch wakeups.</summary>
    public DbSet<CloudRunOutbox> CloudRunOutbox => Set<CloudRunOutbox>();

    /// <summary>Verified GitHub webhook receipts.</summary>
    public DbSet<CloudWebhookDelivery> CloudWebhookDeliveries => Set<CloudWebhookDelivery>();

    /// <summary>Cluster-visible GitHub installation cooldowns.</summary>
    public DbSet<CloudInstallationBudget> CloudInstallationBudgets => Set<CloudInstallationBudget>();

    public DbSet<DeploymentDiagnosticRecord> DeploymentDiagnosticRecords => Set<DeploymentDiagnosticRecord>();

    public DbSet<AiDeploymentAnalysis> AiDeploymentAnalyses => Set<AiDeploymentAnalysis>();

    public DbSet<DeploymentAnalysisWorkItem> DeploymentAnalysisWorkItems => Set<DeploymentAnalysisWorkItem>();

    /// <summary>Durable checkpoints for GitHub Actions diagnostic streaming.</summary>
    public DbSet<GitHubWorkflowCheckpoint> GitHubWorkflowCheckpoints => Set<GitHubWorkflowCheckpoint>();

    /// <summary>Per-job diagnostic checkpoints belonging to GitHub Actions workflow runs.</summary>
    public DbSet<GitHubWorkflowJobCheckpoint> GitHubWorkflowJobCheckpoints => Set<GitHubWorkflowJobCheckpoint>();

    /// <summary>Durable checkpoints for Azure Container Apps console and system log streaming.</summary>
    public DbSet<AzureContainerAppLogCheckpoint> AzureContainerAppLogCheckpoints =>
        Set<AzureContainerAppLogCheckpoint>();

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
        ConfigureApplication(modelBuilder.Entity<Domain.Entities.Application>());
        ConfigureCsProject(modelBuilder.Entity<CsProject>());
        ConfigureCloudDeploymentRuns(modelBuilder);
        ConfigureGitHubWorkflowCheckpoints(modelBuilder);
        ConfigureAzureContainerAppLogCheckpoints(modelBuilder);
        ConfigureDeploymentDiagnosticsAndAnalyses(modelBuilder);
    }

    /// <summary>Indexes durable admission and protects deployment configuration snapshots.</summary>
    private void ConfigureCloudDeploymentRuns(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<CloudDeploymentRun>();
        run.Property(item => item.SnapshotJson)
            .HasConversion(CreateProtectedStringConverter(CloudRunSnapshotProtectorPurpose));
        run.Property(item => item.Phase).HasConversion<string>().HasMaxLength(32);
        run.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
        run.Property(item => item.RepositoryOwner).HasMaxLength(255).IsRequired();
        run.Property(item => item.RepositoryName).HasMaxLength(255).IsRequired();
        run.Property(item => item.BranchName).HasMaxLength(255).IsRequired();
        run.Property(item => item.EnvironmentName).HasMaxLength(100).IsRequired();
        run.Property(item => item.WorkflowFileName).HasMaxLength(255).IsRequired();
        run.Property(item => item.RegistryServer).HasMaxLength(255).IsRequired();
        run.Property(item => item.CommitSha).HasMaxLength(64);
        run.Property(item => item.FailureReason).HasMaxLength(512);
        run.HasIndex(item => new { item.UserId, item.IdempotencyKey }).IsUnique();
        run.HasIndex(item => new { item.Phase, item.NextAttemptAt, item.CreatedAt });
        run.HasIndex(item => new { item.InstallationId, item.RepositoryId, item.CommitSha });
        run.HasIndex(item => new { item.InstallationId, item.Phase, item.LeaseUntil });
        run.HasIndex(item => new { item.UserId, item.Phase, item.LeaseUntil });
        run.HasIndex(item => new { item.UserId, item.LaunchStartedAt });
        run.HasIndex(item => new { item.RepositoryId, item.BranchName, item.EnvironmentName, item.CreatedAt });
        run.HasIndex(item => new { item.ProjectId, item.CreatedAt });
        run.HasOne<Domain.Entities.Application>().WithMany().HasForeignKey(item => item.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        run.HasOne<User>().WithMany().HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var outbox = modelBuilder.Entity<CloudRunOutbox>();
        outbox.HasIndex(item => item.RunId).IsUnique();
        outbox.HasIndex(item => item.DispatchedAt);
        outbox.HasOne<CloudDeploymentRun>().WithOne().HasForeignKey<CloudRunOutbox>(item => item.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        var delivery = modelBuilder.Entity<CloudWebhookDelivery>();
        delivery.Property(item => item.DeliveryId).HasMaxLength(100).IsRequired();
        delivery.Property(item => item.HeadSha).HasMaxLength(64).IsRequired();
        delivery.Property(item => item.HeadBranch).HasMaxLength(255).IsRequired();
        delivery.Property(item => item.WorkflowPath).HasMaxLength(512).IsRequired();
        delivery.Property(item => item.Status).HasMaxLength(32).IsRequired();
        delivery.Property(item => item.Conclusion).HasMaxLength(32);
        delivery.HasIndex(item => item.DeliveryId).IsUnique();
        delivery.HasIndex(item => new { item.ProcessedAt, item.CreatedAt });

        var budget = modelBuilder.Entity<CloudInstallationBudget>();
        budget.HasKey(item => item.InstallationId);
        budget.Property(item => item.InstallationId).ValueGeneratedNever();
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
    private static void ConfigureApplication(EntityTypeBuilder<Domain.Entities.Application> entity)
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

    /// <summary>Configures durable GitHub workflow/job stream checkpoints without retaining raw diagnostic text.</summary>
    private static void ConfigureGitHubWorkflowCheckpoints(ModelBuilder modelBuilder)
    {
        var workflow = modelBuilder.Entity<GitHubWorkflowCheckpoint>();
        workflow.HasIndex(checkpoint => new
        {
            checkpoint.DeploymentId,
            checkpoint.WorkflowRunId,
            checkpoint.WorkflowAttempt
        }).IsUnique();
        workflow.Property(checkpoint => checkpoint.LastWorkflowStateFingerprint).HasMaxLength(128);
        workflow.HasOne(checkpoint => checkpoint.Deployment)
            .WithMany(deployment => deployment.GitHubWorkflowCheckpoints)
            .HasForeignKey(checkpoint => checkpoint.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        var job = modelBuilder.Entity<GitHubWorkflowJobCheckpoint>();
        job.HasIndex(checkpoint => new { checkpoint.GitHubWorkflowCheckpointId, checkpoint.JobId }).IsUnique();
        job.Property(checkpoint => checkpoint.JobName).HasMaxLength(512).IsRequired();
        job.Property(checkpoint => checkpoint.LastStateFingerprint).HasMaxLength(128);
        job.Property(checkpoint => checkpoint.LastLogPrefixHash).HasMaxLength(128);
        job.Property(checkpoint => checkpoint.LastLogContentHash).HasMaxLength(128);
        job.Property(checkpoint => checkpoint.FinalArchiveContentHash).HasMaxLength(128);
        job.HasOne(checkpoint => checkpoint.WorkflowCheckpoint)
            .WithMany(checkpoint => checkpoint.JobCheckpoints)
            .HasForeignKey(checkpoint => checkpoint.GitHubWorkflowCheckpointId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>Configures durable, non-sensitive Azure Container Apps log-tail cursors.</summary>
    private static void ConfigureAzureContainerAppLogCheckpoints(ModelBuilder modelBuilder)
    {
        var checkpoint = modelBuilder.Entity<AzureContainerAppLogCheckpoint>();
        checkpoint.HasIndex(item => new { item.DeploymentId, item.Source }).IsUnique();
        checkpoint.Property(item => item.Source).HasMaxLength(32).IsRequired();
        checkpoint.Property(item => item.LastTieBreaker).HasMaxLength(128);
        checkpoint.HasOne(item => item.Deployment)
            .WithMany(deployment => deployment.AzureContainerAppLogCheckpoints)
            .HasForeignKey(item => item.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureDeploymentDiagnosticsAndAnalyses(ModelBuilder modelBuilder)
    {
        var diagnostic = modelBuilder.Entity<DeploymentDiagnosticRecord>();
        diagnostic.Property(item => item.OrderId).UseIdentityByDefaultColumn();
        diagnostic.HasIndex(item => item.OrderId).IsUnique();
        diagnostic.HasIndex(item => new { item.DeploymentId, item.TimestampUtc, item.Sequence });
        diagnostic.HasIndex(item => new { item.ProjectId, item.DeploymentId, item.OrderId });
        diagnostic.HasIndex(item => item.ExpiresAt);
        diagnostic.Property(item => item.Source).HasMaxLength(64).IsRequired();
        diagnostic.Property(item => item.Kind).HasMaxLength(64).IsRequired();
        diagnostic.Property(item => item.Severity).HasMaxLength(32).IsRequired();
        diagnostic.Property(item => item.Message).IsRequired();
        diagnostic.Property(item => item.TerminalChannel).HasMaxLength(128);
        diagnostic.HasOne(item => item.Deployment).WithMany(deployment => deployment.DiagnosticRecords)
            .HasForeignKey(item => item.DeploymentId).OnDelete(DeleteBehavior.Cascade);

        var analysis = modelBuilder.Entity<AiDeploymentAnalysis>();
        analysis.HasIndex(item => item.IdempotencyKey).IsUnique();
        analysis.HasIndex(item => new { item.DeploymentId, item.CreatedAt });
        analysis.HasIndex(item => item.ExpiresAt);
        analysis.Property(item => item.Provider).HasMaxLength(100).IsRequired();
        analysis.Property(item => item.Model).HasMaxLength(100).IsRequired();
        analysis.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
        analysis.Property(item => item.FailureCode).HasMaxLength(100);
        analysis.HasOne(item => item.Deployment).WithMany(deployment => deployment.AiAnalyses)
            .HasForeignKey(item => item.DeploymentId).OnDelete(DeleteBehavior.Cascade);

        var work = modelBuilder.Entity<DeploymentAnalysisWorkItem>();
        work.HasIndex(item => item.AnalysisId).IsUnique();
        work.HasIndex(item => new { item.CompletedAt, item.ClaimedAt });
        work.HasOne(item => item.Analysis).WithOne().HasForeignKey<DeploymentAnalysisWorkItem>(item => item.AnalysisId)
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
