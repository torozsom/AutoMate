using Domain.Entities.Ai;
using Domain.Entities.Cloud;
using Domain.Entities.Deployments;
using Domain.Entities.Diagnostics;
using Domain.Entities.Projects;
using Domain.Entities.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Data;

/// <summary>
///     Owns the existing entity mappings and protected converters separately from persistence-time audit behavior.
/// </summary>
/// <param name="dataProtectionProvider">Creates converters with the unchanged persisted protection purposes.</param>
internal sealed class AutoMateModelConfiguration(IDataProtectionProvider dataProtectionProvider)
{
    /// <summary>EF discriminator column used for the local and remote user hierarchy.</summary>
    private const string UserTypeDiscriminatorColumn = "user_type";

    /// <summary>Discriminator value stored for GitHub-backed remote users.</summary>
    private const string RemoteUserDiscriminatorValue = "github";

    /// <summary>Discriminator value stored for email/password local users.</summary>
    private const string LocalUserDiscriminatorValue = "local";

    /// <summary>Data Protection purpose string for encrypted GitHub access tokens.</summary>
    private const string GitHubTokenProtectorPurpose = "AutoMate.GitHubTokenProtector";

    /// <summary>Data Protection purpose string for encrypted Azure OAuth tokens.</summary>
    private const string AzureTokenProtectorPurpose = "AutoMate.AzureTokenProtector";

    /// <summary>Separates protected SaaS request snapshots from OAuth token protection.</summary>
    private const string CloudRunSnapshotProtectorPurpose = "AutoMate.CloudRunSnapshot";

    /// <summary>Maximum persisted length for user email addresses.</summary>
    private const int EmailMaxLength = 255;

    /// <summary>Maximum persisted length for user display names.</summary>
    private const int UsernameMaxLength = 100;

    /// <summary>Maximum persisted length for application names.</summary>
    private const int ApplicationNameMaxLength = 200;

    /// <summary>Maximum persisted length for Azure account, tenant, and subscription identifiers.</summary>
    private const int AzureIdentifierMaxLength = 100;

    /// <summary>
    ///     Configures the entity relationships, constraints, and data protection for the
    ///     AutoMateDbContext.
    /// </summary>
    /// <param name="modelBuilder">Builds the documented EF entity mappings or historical model.</param>
    internal void Configure(ModelBuilder modelBuilder)
    {
        ConfigureUserHierarchy(modelBuilder.Entity<User>());
        ConfigureRemoteUser(modelBuilder.Entity<RemoteUser>());
        ConfigureApplication(modelBuilder.Entity<Domain.Entities.Projects.Application>());
        ConfigureCsProject(modelBuilder.Entity<CsProject>());
        ConfigureCloudDeploymentRuns(modelBuilder);
        ConfigureGitHubWorkflowCheckpoints(modelBuilder);
        ConfigureAzureContainerAppLogCheckpoints(modelBuilder);
        ConfigureDeploymentDiagnosticsAndAnalyses(modelBuilder);
        var daily = modelBuilder.Entity<DeploymentDailyTelemetry>();
        daily.HasIndex(d => new { d.DeploymentId, d.DayUtc, d.Container, d.Metric }).IsUnique();
        daily.HasIndex(d => new { d.UserId, d.ProjectId, d.DayUtc });
        daily.Property(d => d.Container).HasMaxLength(128);
        daily.Property(d => d.Metric).HasMaxLength(64);
        daily.Property(d => d.Unit).HasMaxLength(32);
        daily.HasOne<Deployment>().WithMany().HasForeignKey(d => d.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Deployment>().Property(d => d.CloudResourceId).HasMaxLength(1024);
        modelBuilder.Entity<Deployment>().Property(d => d.CloudContainerAppName).HasMaxLength(200);
    }

    /// <summary>
    ///     Indexes durable admission and protects deployment configuration snapshots.
    /// </summary>
    /// <param name="modelBuilder">Builds the documented EF entity mappings or historical model.</param>
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
        run.HasOne<Domain.Entities.Projects.Application>().WithMany().HasForeignKey(item => item.ProjectId)
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
    ///     Configures the shared user table, discriminator, indexes, and application relationship.
    /// </summary>
    /// <param name="entity">EF mapping builder for the documented entity hierarchy and relationships.</param>
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
    /// <param name="entity">EF mapping builder for the documented entity hierarchy and relationships.</param>
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
    /// <param name="entity">EF mapping builder for the documented entity hierarchy and relationships.</param>
    private static void ConfigureApplication(EntityTypeBuilder<Domain.Entities.Projects.Application> entity)
    {
        entity.Property(a => a.Name).HasMaxLength(ApplicationNameMaxLength).IsRequired();
        entity.Property(a => a.RuntimeDiagnosticsEnabled).HasDefaultValue(true);
        entity.Property(a => a.ManagedTelemetryConsent).HasDefaultValue(true);

        entity.HasMany(a => a.CsProjects)
            .WithOne(csp => csp.Application)
            .HasForeignKey(csp => csp.AppId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    ///     Configures C# project configuration and deployment relationships.
    /// </summary>
    /// <param name="entity">EF mapping builder for the documented entity hierarchy and relationships.</param>
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
    ///     Configures durable GitHub workflow/job stream checkpoints without retaining raw
    ///     diagnostic text.
    /// </summary>
    /// <param name="modelBuilder">Builds the documented EF entity mappings or historical model.</param>
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

    /// <summary>
    ///     Configures durable, non-sensitive Azure Container Apps log-tail cursors.
    /// </summary>
    /// <param name="modelBuilder">Builds the documented EF entity mappings or historical model.</param>
    private static void ConfigureAzureContainerAppLogCheckpoints(ModelBuilder modelBuilder)
    {
        var checkpoint = modelBuilder.Entity<AzureContainerAppLogCheckpoint>();
        checkpoint.HasIndex(item => new { item.DeploymentId, item.Source }).IsUnique();
        checkpoint.Property(item => item.Source).HasMaxLength(32).IsRequired();
        checkpoint.Property(item => item.LastTieBreaker).HasMaxLength(128);
        checkpoint.Property(item => item.LastIngestionTieBreaker).HasMaxLength(128);
        checkpoint.HasOne(item => item.Deployment)
            .WithMany(deployment => deployment.AzureContainerAppLogCheckpoints)
            .HasForeignKey(item => item.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    ///     Defines diagnostic, analysis, receipt and budget mappings, indexes and retention
    ///     relationships.
    /// </summary>
    /// <param name="modelBuilder">Builds the documented EF entity mappings or historical model.</param>
    private static void ConfigureDeploymentDiagnosticsAndAnalyses(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeploymentArchiveCleanup>().HasIndex(item => item.ProjectId).IsUnique();
        var diagnostic = modelBuilder.Entity<DeploymentDiagnosticRecord>();
        diagnostic.HasIndex(item => new { item.TenantId, item.OrderId });
        diagnostic.HasIndex(item => item.BufferExpiresAt);
        var tenant = modelBuilder.Entity<TelemetryTenantState>();
        tenant.HasIndex(item => item.TenantId).IsUnique();
        tenant.HasIndex(item => item.DueAt);
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
        analysis.Property(item => item.RequestedModel).HasMaxLength(100);
        analysis.Property(item => item.ModelVersion).HasMaxLength(100);
        analysis.Property(item => item.PromptVersion).HasMaxLength(100);
        analysis.Property(item => item.EstimatedCost).HasPrecision(18, 8);
        analysis.Property(item => item.CostCurrency).HasMaxLength(3);
        analysis.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
        analysis.Property(item => item.FailureCode).HasMaxLength(100);
        analysis.HasOne(item => item.Deployment).WithMany(deployment => deployment.AiAnalyses)
            .HasForeignKey(item => item.DeploymentId).OnDelete(DeleteBehavior.Cascade);

        var receipt = modelBuilder.Entity<AiAnalysisRequest>();
        receipt.HasIndex(item => item.RequestKey).IsUnique();
        receipt.HasIndex(item => new { item.ProjectId, item.AdmissionDay, item.ConsumesQuota });
        receipt.HasIndex(item => item.ExpiresAt);
        receipt.Property(item => item.RequestKey).HasMaxLength(128).IsRequired();
        receipt.HasOne(item => item.Project).WithMany().HasForeignKey(item => item.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        var failure = modelBuilder.Entity<FailedDeploymentAnalysisEvent>();
        failure.HasKey(item => item.DeploymentId);
        failure.HasIndex(item => new { item.CompletedAt, item.CreatedAt });
        failure.HasOne(item => item.Deployment).WithOne()
            .HasForeignKey<FailedDeploymentAnalysisEvent>(item => item.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        var work = modelBuilder.Entity<DeploymentAnalysisWorkItem>();
        var budget = modelBuilder.Entity<AiAnalysisBudgetEntry>();
        budget.HasIndex(item => new { item.TenantId, item.AccountingDay, item.IsProviderAttempt });
        budget.HasIndex(item => new { item.TenantId, item.IsProviderAttempt, item.OccurredAt });
        budget.HasIndex(item => item.OccurredAt);
        budget.HasIndex(item => item.LeaseId).IsUnique();
        budget.Property(item => item.Currency).HasMaxLength(3).IsRequired();
        budget.HasOne(item => item.Tenant).WithMany().HasForeignKey(item => item.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
        work.HasIndex(item => item.AnalysisId).IsUnique();
        work.HasIndex(item => new { item.CompletedAt, item.NextAttemptAt, item.LeaseUntil, item.CreatedAt });
        work.Property(item => item.AttemptCount).HasDefaultValue(0);
        work.Property(item => item.ProviderRetryCount).HasDefaultValue(0);
        work.HasOne(item => item.Analysis).WithOne().HasForeignKey<DeploymentAnalysisWorkItem>(item => item.AnalysisId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    ///     Creates an EF value converter that protects token values before persistence.
    /// </summary>
    /// <param name="purpose">Stable Data Protection purpose separating protected payload types.</param>
    /// <returns>
    ///     The value converter&lt;string?, string?&gt; projection derived from the verified inputs.
    /// </returns>
    private ValueConverter<string?, string?> CreateProtectedStringConverter(string purpose)
    {
        var protector = dataProtectionProvider.CreateProtector(purpose);

        return new ValueConverter<string?, string?>(
            plainText => plainText != null ? protector.Protect(plainText) : null,
            protectedText => protectedText != null ? protector.Unprotect(protectedText) : null);
    }
}
