namespace Application.Ai;

/// <summary>Finite owner-visible skip reasons; provider errors and configuration text never become result guidance.</summary>
public enum AnalysisSkipReason
{
    /// <summary>Missing/disabled/unapproved provider configuration or revoked egress policy.</summary>
    Unavailable,

    /// <summary>Project allowance exhausted before work admission.</summary>
    ProjectQuotaExceeded,

    /// <summary>No supported bounded diagnostic context is available.</summary>
    UnsupportedData,

    /// <summary>Owner-account allowance across projects is exhausted.</summary>
    TenantQuotaExceeded,

    /// <summary>Owner-account rolling admission rate is exhausted.</summary>
    RateLimited,

    /// <summary>Shared provider processing capacity is exhausted.</summary>
    ConcurrencyExceeded,

    /// <summary>Conservative provider spending allowance is absent or exhausted.</summary>
    BudgetExceeded,

    /// <summary>Spending amounts are zero or the attempt bound exceeds the daily ceiling.</summary>
    BudgetNotConfigured,

    /// <summary>The current limit snapshot is invalid.</summary>
    BudgetConfigurationInvalid,

    /// <summary>Today's reservations use a different currency from the current configuration.</summary>
    BudgetCurrencyMismatch
}

/// <summary>Fixed code/message mapping shared by persistence and owner readback, including legacy unavailable results.</summary>
public static class AnalysisSkipPolicy
{
    /// <summary>Returns only bounded authored codes.</summary>
    public static string Code(AnalysisSkipReason reason)
    {
        return reason switch
        {
            AnalysisSkipReason.ProjectQuotaExceeded => "quota_exceeded",
            AnalysisSkipReason.UnsupportedData => "unsupported_data",
            AnalysisSkipReason.TenantQuotaExceeded => "tenant_quota_exceeded",
            AnalysisSkipReason.RateLimited => "rate_limited",
            AnalysisSkipReason.ConcurrencyExceeded => "concurrency_exceeded",
            AnalysisSkipReason.BudgetExceeded => "budget_exceeded",
            AnalysisSkipReason.BudgetNotConfigured => "budget_not_configured",
            AnalysisSkipReason.BudgetConfigurationInvalid => "budget_configuration_invalid",
            AnalysisSkipReason.BudgetCurrencyMismatch => "budget_currency_mismatch",
            _ => "unavailable"
        };
    }

    /// <summary>Ignores any persisted/provider summary and uses authored explanations only.</summary>
    public static string Message(string? code)
    {
        return code switch
        {
            "quota_exceeded" =>
                "AI analysis was skipped because this project's daily analysis allowance has been reached.",
            "unsupported_data" => "AI analysis was skipped because no supported diagnostic data was available.",
            "tenant_quota_exceeded" =>
                "AI analysis was skipped because your account's daily analysis allowance has been reached.",
            "rate_limited" =>
                "AI analysis was skipped because your account's analysis request rate limit has been reached.",
            "concurrency_exceeded" =>
                "AI analysis was skipped because shared provider processing capacity has been reached.",
            "budget_exceeded" =>
                "AI analysis was skipped because your account's daily reserved spending allowance is exhausted. Local and cloud projects share this allowance; it resets at midnight UTC. Reservations are conservative bounds, not actual provider charges.",
            "budget_not_configured" =>
                "AI analysis was skipped because spending limits are not configured for execution. Configure positive AiAnalysis:DailyTenantCostBudget and AiAnalysis:MaximumProviderAttemptCost, with the attempt bound no greater than the daily allowance.",
            "budget_configuration_invalid" =>
                "AI analysis was skipped because the AI spending or shared limit configuration is invalid. Check the AiAnalysis limits and BudgetCurrency.",
            "budget_currency_mismatch" =>
                "AI analysis was skipped because BudgetCurrency differs from today's account reservations. Restore the reservation currency or wait until midnight UTC.",
            _ => "AI analysis was skipped because provider configuration or diagnostic egress approval is unavailable."
        };
    }
}