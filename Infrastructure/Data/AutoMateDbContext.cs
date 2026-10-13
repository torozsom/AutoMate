using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data;

/// <summary>
///     The AutoMateDbContext class is responsible for managing the database context for the
///     AutoMate application. It defines the DbSet properties and configures the relationships,
///     encryption, and constraints for the application entities.
/// </summary>
/// <param name="options">The current validated options used by this operation.</param>
/// <param name="dataProtectionProvider">
///     Protects persisted tokens and secret-bearing launch snapshots.
/// </param>
public class AutoMateDbContext(
    DbContextOptions<AutoMateDbContext> options,
    IDataProtectionProvider dataProtectionProvider) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>Samples audit times without changing persistence-time transition rules.</summary>
    private readonly TimeProvider _clock = TimeProvider.System;

    /// <summary>
    ///     Uses an explicit audit clock while retaining the original context constructor.
    /// </summary>
    /// <param name="options">The existing provider and model options.</param>
    /// <param name="dataProtectionProvider">Creates the unchanged protected converters.</param>
    /// <param name="clock">Samples timestamps at the existing save decision points.</param>
    internal AutoMateDbContext(DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider dataProtectionProvider, TimeProvider clock)
        : this(options, dataProtectionProvider)
    {
        _clock = clock;
    }

    /// <summary>Gets or sets the collection of User entities in the database.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Gets or sets the collection of Application entities in the database.</summary>
    public DbSet<Domain.Entities.Application> Applications => Set<Domain.Entities.Application>();

    /// <summary>Gets or sets the collection of CsProject entities in the database.</summary>
    public DbSet<CsProject> CsProjects => Set<CsProject>();

    /// <summary>Gets or sets the collection of ProjectConfiguration entities in the database.</summary>
    public DbSet<Configuration> AppConfigs => Set<Configuration>();

    /// <summary>Gets or sets the collection of Deployment entities in the database.</summary>
    public DbSet<Deployment> Deployments => Set<Deployment>();

    /// <summary>Archive deletion outbox without cascading owner/project foreign keys.</summary>
    public DbSet<DeploymentArchiveCleanup> DeploymentArchiveCleanups => Set<DeploymentArchiveCleanup>();

    /// <summary>Durable SaaS cloud deployment requests.</summary>
    public DbSet<CloudDeploymentRun> CloudDeploymentRuns => Set<CloudDeploymentRun>();

    /// <summary>Transactional launch wakeups.</summary>
    public DbSet<CloudRunOutbox> CloudRunOutbox => Set<CloudRunOutbox>();

    /// <summary>Verified GitHub webhook receipts.</summary>
    public DbSet<CloudWebhookDelivery> CloudWebhookDeliveries => Set<CloudWebhookDelivery>();

    /// <summary>Cluster-visible GitHub installation cooldowns.</summary>
    public DbSet<CloudInstallationBudget> CloudInstallationBudgets => Set<CloudInstallationBudget>();

    /// <summary>Tracked deployment diagnostic records metadata exposed for persistence queries.</summary>
    public DbSet<DeploymentDiagnosticRecord> DeploymentDiagnosticRecords => Set<DeploymentDiagnosticRecord>();

    /// <summary>Tracked deployment daily telemetry metadata exposed for persistence queries.</summary>
    public DbSet<DeploymentDailyTelemetry> DeploymentDailyTelemetry => Set<DeploymentDailyTelemetry>();

    /// <summary>Specialized telemetry delivery leases and loss accounting.</summary>
    public DbSet<TelemetryTenantState> TelemetryTenantStates => Set<TelemetryTenantState>();

    /// <summary>Tracked ai deployment analyses metadata exposed for persistence queries.</summary>
    public DbSet<AiDeploymentAnalysis> AiDeploymentAnalyses => Set<AiDeploymentAnalysis>();

    /// <summary>Durable metadata-only admission receipts independent of result deletion.</summary>
    public DbSet<AiAnalysisRequest> AiAnalysisRequests => Set<AiAnalysisRequest>();

    /// <summary>Transactional failure wakeups, containing no diagnostic payloads.</summary>
    public DbSet<FailedDeploymentAnalysisEvent> FailedDeploymentAnalysisEvents => Set<FailedDeploymentAnalysisEvent>();

    /// <summary>Tracked deployment analysis work items metadata exposed for persistence queries.</summary>
    public DbSet<DeploymentAnalysisWorkItem> DeploymentAnalysisWorkItems => Set<DeploymentAnalysisWorkItem>();

    /// <summary>
    ///     Tenant-wide admission and non-refundable provider reservations, independent of
    ///     result/project lifetime.
    /// </summary>
    public DbSet<AiAnalysisBudgetEntry> AiAnalysisBudgetEntries => Set<AiAnalysisBudgetEntry>();

    /// <summary>Durable checkpoints for GitHub Actions diagnostic streaming.</summary>
    public DbSet<GitHubWorkflowCheckpoint> GitHubWorkflowCheckpoints => Set<GitHubWorkflowCheckpoint>();

    /// <summary>Per-job diagnostic checkpoints belonging to GitHub Actions workflow runs.</summary>
    public DbSet<GitHubWorkflowJobCheckpoint> GitHubWorkflowJobCheckpoints => Set<GitHubWorkflowJobCheckpoint>();

    /// <summary>Durable checkpoints for Azure Container Apps console and system log streaming.</summary>
    public DbSet<AzureContainerAppLogCheckpoint> AzureContainerAppLogCheckpoints =>
        Set<AzureContainerAppLogCheckpoint>();

    /// <summary>
    ///     Gets or sets the collection of DataProtectionKey entities in the database. Required for
    ///     distributed data protection (e.g., across Docker containers).
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();


    /// <summary>
    ///     Applies the stable mappings after the base context model configuration.
    /// </summary>
    /// <param name="modelBuilder">Builds the existing persistence model.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        new AutoMateModelConfiguration(dataProtectionProvider).Configure(modelBuilder);
    }

    /// <summary>
    ///     Overrides the SaveChangesAsync method to automatically set the CreatedAt and UpdatedAt
    ///     properties.
    /// </summary>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateAuditFields();
        return base.SaveChangesAsync(cancellationToken);
    }


    /// <summary>
    ///     Captures failed-deployment analysis wakeups atomically with asynchronous metadata
    ///     persistence.
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">
    ///     Whether successful persistence accepts the tracked entity changes.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>The number of persisted entity entries, including captured failure-wakeup metadata.</returns>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        UpdateAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }


    /// <summary>
    ///     Overrides the synchronous SaveChanges method to ensure audit fields are updated even if
    ///     called synchronously.
    /// </summary>
    /// <returns>The number of state entries written to the database.</returns>
    public override int SaveChanges()
    {
        UpdateAuditFields();
        return base.SaveChanges();
    }


    /// <summary>
    ///     Captures failed-deployment analysis wakeups with the tracked metadata changes.
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">
    ///     Whether successful persistence accepts the tracked entity changes.
    /// </param>
    /// <returns>The number of persisted entity entries, including captured failure-wakeup metadata.</returns>
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
        foreach (var entry in ChangeTracker.Entries<Deployment>()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified))
            if (entry.Entity.Outcome == DeploymentOutcome.Unknown &&
                entry.Entity.Status is DeploymentStatus.Running or DeploymentStatus.Failed)
            {
                entry.Entity.Outcome = entry.Entity.Status == DeploymentStatus.Running
                    ? DeploymentOutcome.Succeeded
                    : DeploymentOutcome.Failed;
                entry.Entity.FinishedAt ??= _clock.GetUtcNow();
            }

        var entries = ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified);

        var now = _clock.GetUtcNow();

        foreach (var entry in entries)
        {
            entry.Entity.UpdatedAt = now;
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = now;
        }
    }
}
