using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Abstractions.GitHub;
using Application.Diagnostics;
using Application.Orchestration;
using Domain.Defaults;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>Authorizes and atomically admits a credential-free SaaS deployment.</summary>
public sealed class CloudDeploymentRunService(
    AutoMateDbContext dbContext,
    IGitHubAppCredentials githubApp,
    IOptions<CloudSaasOptions> options) : ICloudDeploymentRunService
{
    /// <summary>Accepted Azure Container Registry login server shape.</summary>
    private static readonly Regex RegistryPattern = new(
        "^[a-z0-9][a-z0-9-]*\\.azurecr\\.io$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public async Task<CloudDeploymentReceipt> StartAsync(CloudDeploymentStart start,
        CancellationToken cancellationToken = default)
    {
        if (start.UserId == Guid.Empty || start.ProjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(start.IdempotencyKey) || start.IdempotencyKey.Length > 128)
            throw new ArgumentException("A valid user, project, and idempotency key are required.");
        var previous = await dbContext.CloudDeploymentRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == start.UserId &&
                                          item.IdempotencyKey == start.IdempotencyKey, cancellationToken);
        if (previous is not null) return ReceiptForSameRequest(previous, start);

        var project = await dbContext.Applications.AsNoTracking()
                          .SingleOrDefaultAsync(item => item.Id == start.ProjectId && item.UserId == start.UserId &&
                                                        item.SourceType == SourceType.Remote, cancellationToken)
                      ?? throw new UnauthorizedAccessException("The cloud project is unavailable to this user.");
        if (!Uri.TryCreate(project.SourcePathOrUrl, UriKind.Absolute, out var uri) ||
            uri.Host != "github.com" ||
            !uri.AbsolutePath.Trim('/').TrimEnd('/').Equals(
                $"{start.RepositoryOwner}/{start.RepositoryName}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The repository does not match the selected project.");
        if (start.Config.ProjectId != start.ProjectId)
            throw new InvalidOperationException("Deployment configuration does not match the project.");
        var hasAzureConnection = await dbContext.Users.OfType<RemoteUser>().AsNoTracking()
            .AnyAsync(item => item.Id == start.UserId && item.AzureRefreshToken != null &&
                              item.AzureTenantId != null && item.AzureSubscriptionId != null,
                cancellationToken);
        if (!hasAzureConnection)
            throw new InvalidOperationException("Reconnect Azure with offline access before deploying.");
        var registry = start.Config.CloudRegistryName.Trim().ToLowerInvariant();
        if (!RegistryPattern.IsMatch(registry))
            throw new InvalidOperationException("Choose a customer-owned Azure Container Registry login server.");
        var (installationId, repositoryId) = await githubApp.ResolveRepositoryAsync(
            start.UserGitHubAccessToken, start.RepositoryOwner, start.RepositoryName, cancellationToken);

        for (var attempt = 0; attempt < 3; attempt++)
            try
            {
                return await AdmitAsync(start, registry, installationId, repositoryId, cancellationToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or PostgresException)
            {
                dbContext.ChangeTracker.Clear();
                previous = await dbContext.CloudDeploymentRuns.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.UserId == start.UserId &&
                                                  item.IdempotencyKey == start.IdempotencyKey,
                        cancellationToken);
                if (previous is not null) return ReceiptForSameRequest(previous, start);
                if (ex is not PostgresException { SqlState: "40001" } || attempt == 2) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }

        throw new InvalidOperationException("Cloud deployment admission could not be completed.");
    }

    /// <inheritdoc />
    public async Task<CloudDeploymentReceipt?> GetAsync(Guid userId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        var run = await dbContext.CloudDeploymentRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == runId && item.UserId == userId, cancellationToken);
        return run is null ? null : Receipt(run);
    }

    /// <inheritdoc />
    public async Task<CloudDeploymentReceipt?> GetLatestForProjectAsync(Guid userId, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var run = await dbContext.CloudDeploymentRuns.AsNoTracking()
            .Where(item => item.UserId == userId && item.ProjectId == projectId)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : Receipt(run);
    }

    /// <summary>Checks the queue limit and persists the run and wakeup in one serializable transaction.</summary>
    private async Task<CloudDeploymentReceipt> AdmitAsync(CloudDeploymentStart start, string registry,
        long installationId, long repositoryId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var previous = await dbContext.CloudDeploymentRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == start.UserId &&
                                          item.IdempotencyKey == start.IdempotencyKey, cancellationToken);
        if (previous is not null) return ReceiptForSameRequest(previous, start);
        var queued = await dbContext.CloudDeploymentRuns.CountAsync(item =>
            item.UserId == start.UserId && item.Phase == CloudRunPhase.Queued, cancellationToken);
        if (queued >= options.Value.MaxQueuedPerUser)
        {
            AutoMateTelemetry.CloudRunsRejected.Add(1);
            throw new InvalidOperationException(
                "Your cloud deployment queue is full. Try again in about 30 seconds.");
        }

        var snapshot = JsonSerializer.Serialize(new CloudRunSnapshot(start.Config, start.Metadata,
            start.CsProjectName, start.RepositoryRoot));
        if (snapshot.Length > 262_144)
            throw new InvalidOperationException("Cloud deployment configuration is too large.");
        var run = new CloudDeploymentRun
        {
            UserId = start.UserId,
            ProjectId = start.ProjectId,
            IdempotencyKey = start.IdempotencyKey,
            InstallationId = installationId,
            RepositoryId = repositoryId,
            RepositoryOwner = start.RepositoryOwner,
            RepositoryName = start.RepositoryName,
            BranchName = DeploymentDefaults.CloudDeploymentBranchName,
            EnvironmentName = start.Config.EnvironmentName,
            WorkflowFileName = DeploymentDefaults.CloudWorkflowFileName,
            RegistryServer = registry,
            SnapshotJson = snapshot,
            NextAttemptAt = DateTimeOffset.UtcNow
        };
        dbContext.CloudDeploymentRuns.Add(run);
        dbContext.CloudRunOutbox.Add(new CloudRunOutbox { RunId = run.Id });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        AutoMateTelemetry.CloudRunsAdmitted.Add(1);
        return Receipt(run);
    }

    /// <summary>Projects a public receipt without exposing protected configuration.</summary>
    private static CloudDeploymentReceipt Receipt(CloudDeploymentRun run)
    {
        return new CloudDeploymentReceipt(run.Id, run.Phase, run.CreatedAt, run.FailureReason);
    }

    /// <summary>Prevents one idempotency key from silently representing a different target.</summary>
    private static CloudDeploymentReceipt ReceiptForSameRequest(CloudDeploymentRun run,
        CloudDeploymentStart start)
    {
        if (run.ProjectId != start.ProjectId ||
            !string.Equals(run.RepositoryOwner, start.RepositoryOwner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(run.RepositoryName, start.RepositoryName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The idempotency key belongs to a different deployment request.");
        return Receipt(run);
    }
}