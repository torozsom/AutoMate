using System.Data;
using Application.Abstractions.Ai;
using Application.Ai;
using Application.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Work = Application.Abstractions.Ai.DeploymentAnalysisWorkItem;

namespace Infrastructure.Ai;

/// <summary>Serializes short metadata reservations; no database lock is held during context or provider execution.</summary>
public sealed class AnalysisBudgetGuard(
    AutoMateDbContext db,
    IOptionsMonitor<AiAnalysisOptions> options,
    TimeProvider clock,
    ILogger<AnalysisBudgetGuard>? logger = null) : IAnalysisBudgetGuard
{
    /// <inheritdoc />
    public async Task<AnalysisSkipReason?> ReserveAttemptAsync(Work work, CancellationToken cancellationToken = default)
    {
        await using var transaction = db.Database.IsNpgsql()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : await db.Database.BeginTransactionAsync(cancellationToken);
        if (db.Database.IsNpgsql())
            // All instances use one transaction-scoped guard for the shared concurrency invariant.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(716204950121)", cancellationToken);
        else
            // SQLite's write transaction provides the equivalent serialization in relational regression fixtures.
            await db.Users.Where(item => item.Id != Guid.Empty)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Username, item => item.Username),
                    cancellationToken);
        var settings = options.CurrentValue;
        var now = clock.GetUtcNow();
        if (!AnalysisBudgetPolicy.IsValid(settings)) return Deny(work, AnalysisSkipReason.BudgetConfigurationInvalid);
        var tenant = await db.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == work.AnalysisId &&
                                                                        item.LeaseId == work.LeaseId &&
                                                                        item.LeaseUntil > now &&
                                                                        item.CompletedAt == null &&
                                                                        item.Analysis.ExpiresAt > now &&
                                                                        item.Analysis.Status ==
                                                                        AiAnalysisStatus.Running)
            .Select(item => (Guid?)item.Analysis.Deployment.CsProject!.Application.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenant is null) return AnalysisSkipReason.Unavailable;
        if (await db.AiAnalysisBudgetEntries.AnyAsync(item => item.Id == work.LeaseId &&
                                                              item.TenantId == tenant &&
                                                              item.AnalysisId == work.AnalysisId &&
                                                              item.IsProviderAttempt,
                cancellationToken)) return null;
        var active = db.AiAnalysisBudgetEntries.Where(entry => entry.IsProviderAttempt &&
                                                               db.DeploymentAnalysisWorkItems.Any(item =>
                                                                   item.AnalysisId == entry.AnalysisId &&
                                                                   item.LeaseId == entry.LeaseId &&
                                                                   item.LeaseUntil > now && item.CompletedAt == null &&
                                                                   item.Analysis.ExpiresAt > now &&
                                                                   item.Analysis.Status == AiAnalysisStatus.Running));
        if (await active.CountAsync(cancellationToken) >= settings.MaximumGlobalProviderConcurrency ||
            await active.CountAsync(item => item.TenantId == tenant, cancellationToken) >=
            settings.MaximumTenantProviderConcurrency) return AnalysisSkipReason.ConcurrencyExceeded;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var charges = db.AiAnalysisBudgetEntries.Where(item => item.TenantId == tenant &&
                                                               item.AccountingDay == day && item.IsProviderAttempt);
        var units = AnalysisBudgetPolicy.Units(settings.MaximumProviderAttemptCost);
        var daily = AnalysisBudgetPolicy.Units(settings.DailyTenantCostBudget);
        if (units <= 0 || daily <= 0 || units > daily)
            return Deny(work, AnalysisSkipReason.BudgetNotConfigured, daily, attempt: units);
        var used = await charges.SumAsync(item => item.ReservedCostUnits, cancellationToken);
        if (await charges.AnyAsync(item => item.Currency != settings.BudgetCurrency, cancellationToken))
            return Deny(work, AnalysisSkipReason.BudgetCurrencyMismatch, daily, used, units);
        if (units > daily - used)
            return Deny(work, AnalysisSkipReason.BudgetExceeded, daily, used, units);
        if (!ReferenceEquals(settings, options.CurrentValue)) return AnalysisSkipReason.Unavailable;
        var charge = new AiAnalysisBudgetEntry
        {
            Id = work.LeaseId,
            TenantId = tenant.Value,
            AnalysisId = work.AnalysisId,
            LeaseId = work.LeaseId,
            IsProviderAttempt = true,
            AccountingDay = day,
            OccurredAt = now,
            ReservedCostUnits = units,
            Currency = settings.BudgetCurrency
        };
        db.AiAnalysisBudgetEntries.Add(charge);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            db.Entry(charge).State = EntityState.Detached;
        }

        return null;
    }

    /// <summary>Reports finite denial and integer monetary units with deployment/analysis correlation only.</summary>
    private AnalysisSkipReason Deny(Work work, AnalysisSkipReason reason, long daily = 0, long used = 0,
        long attempt = 0)
    {
        if (logger is not null)
        {
            using var correlation = OperationalLog.BeginCorrelation(logger, work.DeploymentId, work.AnalysisId);
            logger.LogWarning(
                "AI spending reservation denied: {BudgetReason}; daily allowance {DailyBudgetUnits}, reserved {ReservedCostUnits}, attempt {AttemptCostUnits} monetary units.",
                reason, daily, used, attempt);
        }

        return reason;
    }

    /// <summary>Called while the owner-account admission lock is held; result/project deletion cannot refund these counts.</summary>
    internal static async Task<AnalysisSkipReason?> AdmissionReasonAsync(AutoMateDbContext db, Guid tenant,
        AiAnalysisOptions settings, DateTimeOffset now, CancellationToken token)
    {
        if (!AnalysisBudgetPolicy.IsValid(settings)) return AnalysisSkipReason.BudgetExceeded;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var entries = db.AiAnalysisBudgetEntries.Where(item => item.TenantId == tenant && !item.IsProviderAttempt);
        if (await entries.CountAsync(item => item.AccountingDay == day, token) >= settings.DailyTenantLimit)
            return AnalysisSkipReason.TenantQuotaExceeded;
        if (await entries.CountAsync(item => item.OccurredAt > now.AddSeconds(-60), token) >=
            settings.TenantRequestsPerMinute)
            return AnalysisSkipReason.RateLimited;
        return null;
    }
}