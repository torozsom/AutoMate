using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Ai;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

public sealed class DeploymentAnalysisWorker(IServiceScopeFactory scopeFactory,
    ILogger<DeploymentAnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisQueue>();
                var work = await queue.ClaimNextAsync(stoppingToken);
                if (work is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                await ProcessAsync(scope.ServiceProvider, work.AnalysisId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Deployment analysis worker recovered from an error."); }
        }
    }

    private static async Task ProcessAsync(IServiceProvider services, Guid analysisId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AutoMateDbContext>();
        var analysis = await db.AiDeploymentAnalyses.FirstAsync(item => item.Id == analysisId, cancellationToken);
        analysis.Status = AiAnalysisStatus.Running; analysis.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var context = await services.GetRequiredService<IDeploymentDiagnosticStore>()
                .BuildContextAsync(analysis.DeploymentId, services.GetRequiredService<IOptions<AiAnalysisOptions>>().Value.MaximumContextCharacters, cancellationToken);
            if (string.IsNullOrWhiteSpace(context)) throw new InvalidOperationException("No diagnostic data is available.");
            var response = await services.GetRequiredService<ILlmAnalysisProvider>().AnalyzeAsync(new(context), cancellationToken);
            analysis.Status = AiAnalysisStatus.Completed; analysis.Provider = response.Provider; analysis.Model = response.Model;
            analysis.Summary = response.Summary; analysis.RecommendedStepsJson = JsonSerializer.Serialize(response.RecommendedSteps);
            analysis.EvidenceReferencesJson = JsonSerializer.Serialize(response.EvidenceReferences); analysis.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (InvalidOperationException exception)
        {
            analysis.Status = AiAnalysisStatus.Skipped; analysis.FailureCode = "unavailable"; analysis.Summary = exception.Message; analysis.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception)
        {
            analysis.Status = AiAnalysisStatus.Failed; analysis.FailureCode = "provider_failure"; analysis.CompletedAt = DateTimeOffset.UtcNow;
        }
        var work = await db.DeploymentAnalysisWorkItems.FirstAsync(item => item.AnalysisId == analysisId, cancellationToken);
        work.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
