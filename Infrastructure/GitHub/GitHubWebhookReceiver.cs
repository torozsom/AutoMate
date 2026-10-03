using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.GitHub;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.GitHub;

/// <summary>Authenticates GitHub events and persists only correlation metadata.</summary>
public sealed class GitHubWebhookReceiver(AutoMateDbContext dbContext, IOptions<GitHubAppOptions> options)
    : IGitHubWebhookReceiver
{
    /// <inheritdoc />
    public async Task ReceiveAsync(ReadOnlyMemory<byte> body, string signature, string deliveryId,
        string eventName, CancellationToken cancellationToken)
    {
        if (body.Length == 0 || body.Length > 1_048_576 || deliveryId.Length is 0 or > 100 ||
            !signature.StartsWith("sha256=", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid GitHub webhook delivery.");
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.WebhookSecret), body.Span);
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature[7..]);
        }
        catch (FormatException)
        {
            throw new UnauthorizedAccessException("Invalid GitHub webhook signature.");
        }

        if (provided.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(expected, provided))
            throw new UnauthorizedAccessException("Invalid GitHub webhook signature.");
        if (eventName != "workflow_run") return;
        if (await dbContext.CloudWebhookDeliveries.AnyAsync(item => item.DeliveryId == deliveryId,
                cancellationToken)) return;

        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        var action = root.GetProperty("action").GetString();
        if (action is not ("requested" or "in_progress" or "completed")) return;
        var workflow = root.GetProperty("workflow_run");
        var delivery = new CloudWebhookDelivery
        {
            DeliveryId = deliveryId,
            InstallationId = root.GetProperty("installation").GetProperty("id").GetInt64(),
            RepositoryId = root.GetProperty("repository").GetProperty("id").GetInt64(),
            WorkflowRunId = workflow.GetProperty("id").GetInt64(),
            WorkflowAttempt = workflow.TryGetProperty("run_attempt", out var attempt) ? attempt.GetInt32() : 1,
            HeadSha = workflow.GetProperty("head_sha").GetString() ?? string.Empty,
            HeadBranch = workflow.GetProperty("head_branch").GetString() ?? string.Empty,
            WorkflowPath = workflow.GetProperty("path").GetString() ?? string.Empty,
            Status = workflow.GetProperty("status").GetString() ?? string.Empty,
            Conclusion = workflow.TryGetProperty("conclusion", out var conclusion) ? conclusion.GetString() : null
        };
        if (!await dbContext.CloudDeploymentRuns.AsNoTracking().AnyAsync(item =>
                item.InstallationId == delivery.InstallationId && item.RepositoryId == delivery.RepositoryId &&
                item.Phase != CloudRunPhase.Succeeded &&
                item.Phase != CloudRunPhase.Failed &&
                item.Phase != CloudRunPhase.TimedOut, cancellationToken))
            return;
        dbContext.CloudWebhookDeliveries.Add(delivery);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Redelivery races are expected across app instances; the unique delivery ID wins.
            if (!await dbContext.CloudWebhookDeliveries.AsNoTracking()
                    .AnyAsync(item => item.DeliveryId == deliveryId, cancellationToken)) throw;
        }
    }
}