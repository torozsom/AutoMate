using Application.Abstractions.Ai;
using Application.Ai;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>Read-only local readiness checks; never resolves an LLM adapter, reads diagnostics or changes queue ownership.</summary>
public sealed class DeploymentAnalysisReadinessService(
    AutoMateDbContext db,
    IOptionsMonitor<AiAnalysisOptions> options,
    AnalysisProviderCatalog catalog) : IDeploymentAnalysisReadiness
{
    /// <inheritdoc />
    public async Task<DeploymentAnalysisReadiness> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = ReadConfiguration();
        try
        {
            // LIMIT 1 verifies the actual lease/retry and analysis columns even when the queue is empty.
            // Automatic capture/dispatch and retention run even while AI admission is disabled.
            await db.DeploymentAnalysisWorkItems.AsNoTracking().Select(item => new
            {
                item.Id,
                item.AnalysisId,
                item.CreatedAt,
                item.ClaimedAt,
                item.LeaseId,
                item.LeaseUntil,
                item.AttemptCount,
                item.ProviderRetryCount,
                item.NextAttemptAt,
                item.CompletedAt,
                item.Analysis.Status,
                item.Analysis.ExpiresAt
            }).Take(1).ToListAsync(cancellationToken);
            await db.FailedDeploymentAnalysisEvents.AsNoTracking()
                .Select(item => new { item.DeploymentId, item.CreatedAt, item.CompletedAt })
                .Take(1).ToListAsync(cancellationToken);
            await db.AiAnalysisBudgetEntries.AsNoTracking()
                .Select(item => new
                {
                    item.Id,
                    item.TenantId,
                    item.AnalysisId,
                    item.LeaseId,
                    item.IsProviderAttempt,
                    item.AccountingDay,
                    item.OccurredAt,
                    item.ReservedCostUnits,
                    item.Currency
                })
                .Take(1).ToListAsync(cancellationToken);
            return new DeploymentAnalysisReadiness(configuration, true);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Translate dependency/schema failures without retaining sensitive exception or connection details.
            return new DeploymentAnalysisReadiness(configuration, false);
        }
    }

    /// <summary>Uses current validated policy and local credential presence only; reload failures become finite states.</summary>
    private AnalysisConfigurationState ReadConfiguration()
    {
        try
        {
            var settings = options.CurrentValue;
            if (!settings.Enabled) return AnalysisConfigurationState.Disabled;
            if (!settings.ProviderEgressEnabled) return AnalysisConfigurationState.EgressDisabled;
            var provider = catalog.Select(settings);
            if (!AnalysisBudgetPolicy.IsValid(settings)) return AnalysisConfigurationState.Invalid;
            if (settings.DailyTenantCostBudget <= 0 || settings.MaximumProviderAttemptCost <= 0 ||
                settings.MaximumProviderAttemptCost > settings.DailyTenantCostBudget)
                return AnalysisConfigurationState.Unavailable;
            return provider?.CredentialsConfigured?.Invoke() == true
                ? AnalysisConfigurationState.Ready
                : AnalysisConfigurationState.Unavailable;
        }
        catch (OptionsValidationException)
        {
            return AnalysisConfigurationState.Invalid;
        }
        catch (Exception)
        {
            return AnalysisConfigurationState.Unavailable;
        }
    }
}