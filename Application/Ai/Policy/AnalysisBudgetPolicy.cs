namespace Application.Ai.Policy;

/// <summary>
///     Validates shared limit snapshots and converts exact eight-decimal monetary allowances to
///     integer units.
/// </summary>
public static class AnalysisBudgetPolicy
{
    /// <summary>
    ///     Integer monetary units per currency unit; no floating-point budget arithmetic is used.
    /// </summary>
    public const long UnitsPerCurrency = 100_000_000;

    /// <summary>
    ///     Validates independent limits even when AI is disabled; zero spend is a valid deny-
    ///     execution policy.
    /// </summary>
    /// <param name="settings">The analysis options whose bounds are validated.</param>
    /// <returns>Whether every configured admission and monetary limit satisfies the supported bounds.</returns>
    public static bool IsValid(AiAnalysisOptions settings)
    {
        return settings.DailyTenantLimit is >= 0 and <= 100_000 &&
               settings.TenantRequestsPerMinute is >= 0 and <= 1_000 &&
               settings.MaximumTenantProviderConcurrency is >= 0 and <= 256 &&
               settings.MaximumGlobalProviderConcurrency is >= 0 and <= 4096 &&
               settings.MaximumTenantProviderConcurrency <= settings.MaximumGlobalProviderConcurrency &&
               ValidMoney(settings.DailyTenantCostBudget) && ValidMoney(settings.MaximumProviderAttemptCost) &&
               settings.BudgetCurrency is { Length: 3 } && settings.BudgetCurrency.All(c => c is >= 'A' and <= 'Z');
    }

    /// <summary>
    ///     Returns exact integer units for validated amounts; callers must validate the snapshot
    ///     first.
    /// </summary>
    /// <param name="amount">The monetary amount in the configured currency.</param>
    /// <returns>The checked integer amount in hundred-millionths of the configured currency.</returns>
    /// <exception cref="OverflowException">
    ///     Scaling or converting the amount exceeds the supported numeric range.
    /// </exception>
    public static long Units(decimal amount)
    {
        return checked((long)(amount * UnitsPerCurrency));
    }

    /// <summary>
    ///     Bounds amounts and rejects precision that could otherwise be silently rounded down.
    /// </summary>
    /// <param name="amount">The monetary amount in the configured currency.</param>
    /// <returns>Whether the nonnegative amount fits the supported precision and storage range.</returns>
    private static bool ValidMoney(decimal amount)
    {
        return amount is >= 0 and <= 1_000_000 &&
               decimal.Truncate(amount * UnitsPerCurrency) == amount * UnitsPerCurrency;
    }
}
