using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Ai;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

public sealed class DeploymentAnalysisService(AutoMateDbContext dbContext, IOptions<AiAnalysisOptions> options)
    : IDeploymentAnalysisService
{
    private static readonly TimeSpan ResultRetention = TimeSpan.FromDays(90);

    public async Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled) return new(false, "AI analysis is disabled by the operator.");
        var deployment = await dbContext.Deployments.Include(item => item.CsProject).ThenInclude(item => item!.Application)
            .Include(item => item.CsProject).ThenInclude(item => item!.Configuration)
            .FirstOrDefaultAsync(item => item.Id == deploymentId, cancellationToken);
        if (deployment?.CsProject?.Application?.UserId != ownerId) return new(false, "Deployment not found.");
        if (deployment.CsProject.Configuration?.AiDiagnosticEgressConsented != true)
            return new(false, "Enable AI diagnostic data egress in project configuration before requesting analysis.");
        var since = DateTimeOffset.UtcNow.Date;
        var projectId = deployment.CsProjectId;
        var used = await dbContext.AiDeploymentAnalyses.CountAsync(item => item.Deployment.CsProjectId == projectId && item.CreatedAt >= since, cancellationToken);
        if (used >= settings.DailyProjectLimit) return new(false, "The daily analysis limit for this project has been reached.");
        var existing = await dbContext.AiDeploymentAnalyses.OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(item => item.DeploymentId == deploymentId &&
                (item.Status == AiAnalysisStatus.Queued || item.Status == AiAnalysisStatus.Running), cancellationToken);
        if (existing is not null) return new(true, "An analysis is already in progress.", ToView(existing));
        var analysis = new AiDeploymentAnalysis
        {
            DeploymentId = deploymentId, Trigger = AiAnalysisTrigger.Manual, Status = AiAnalysisStatus.Queued,
            Provider = "openai", Model = settings.Model, IdempotencyKey = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.UtcNow.Add(ResultRetention)
        };
        dbContext.AiDeploymentAnalyses.Add(analysis);
        dbContext.DeploymentAnalysisWorkItems.Add(new Domain.Entities.DeploymentAnalysisWorkItem { Analysis = analysis });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(true, "Analysis queued.", ToView(analysis));
    }

    public async Task<DeploymentAnalysisView?> GetLatestAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        var analysis = await dbContext.AiDeploymentAnalyses.AsNoTracking()
            .Where(item => item.DeploymentId == deploymentId && item.Deployment.CsProject!.Application!.UserId == ownerId)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return analysis is null ? null : ToView(analysis);
    }

    private static DeploymentAnalysisView ToView(AiDeploymentAnalysis item) => new(item.Id, item.DeploymentId, item.Status,
        item.Trigger, item.Summary, Deserialize(item.RecommendedStepsJson), Deserialize(item.EvidenceReferencesJson),
        item.FailureCode, item.CreatedAt, item.CompletedAt);
    private static IReadOnlyList<string> Deserialize(string? value) => string.IsNullOrWhiteSpace(value) ? [] :
        JsonSerializer.Deserialize<string[]>(value) ?? [];
}
