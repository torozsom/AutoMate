using Application.Abstractions.Ai.Policy;
using Application.Ai.Policy;
using Domain.Enums.Ai;
using Microsoft.Extensions.Options;

namespace Test.Infra.Ai.Admission;

/// <summary>
///     Shared synthetic approvals, reloadable options and authorization fixtures for integration tests.
/// </summary>
internal static class AnalysisPolicyTestSupport
{
    /// <summary>
    ///     Creates explicit synthetic approvals; these never enable any deployed environment.
    /// </summary>
    /// <param name="tenant">Synthetic owner identity.</param>
    /// <param name="enabled">Whether to include the option.</param>
    /// <param name="endpoint">Parsed HTTP(S) collector endpoint, or null when invalid or absent.</param>
    /// <param name="region">Synthetic provider region.</param>
    /// <param name="categories">Diagnostic categories approved in the synthetic configuration.</param>
    /// <param name="egressEnabled">Whether synthetic provider egress is enabled.</param>
    /// <param name="regionalApproval">Whether the selected provider region has operator approval in the fixture.</param>
    /// <param name="provider">Selected synthetic provider identity, or null to use fixture defaults.</param>
    /// <param name="approvedRegions">Approved region catalog, or null to use fixture defaults.</param>
    /// <param name="retentionDays">Configured result retention in days.</param>
    /// <param name="model">The provider-returned model identity, if known.</param>
    /// <param name="dailyLimit">Configured daily request allowance.</param>
    /// <param name="automatic">Whether to exercise automatic rather than manual admission.</param>
    /// <param name="concurrency">Configured parallel-operation limit.</param>
    /// <param name="tenantLimit">Configured per-tenant allowance.</param>
    /// <param name="rateLimit">Configured request-rate allowance.</param>
    /// <param name="tenantConcurrency">Configured per-tenant provider concurrency.</param>
    /// <param name="globalConcurrency">Configured global provider concurrency.</param>
    /// <param name="dailyBudget">Synthetic daily spending ceiling.</param>
    /// <param name="attemptCost">Conservative synthetic per-attempt charge.</param>
    /// <param name="currency">Configured budget currency.</param>
    /// <returns>The configured ai analysis options instance or response for this scenario.</returns>
    internal static AiAnalysisOptions Approved(Guid tenant, bool enabled = true,
        string endpoint = "https://eu.api.openai.com/v1/",
        string region = "eu", string[]? categories = null, bool egressEnabled = true,
        bool regionalApproval = true, string? provider = "openai", string[]? approvedRegions = null,
        int retentionDays = 90, string model = "gpt-5-mini", int dailyLimit = 5, bool automatic = false,
        int concurrency = 1, int tenantLimit = 100_000, int rateLimit = 1000,
        int tenantConcurrency = 256, int globalConcurrency = 4096, decimal dailyBudget = 1000,
        decimal attemptCost = 1, string currency = "USD")
    {
        return new AiAnalysisOptions
        {
            Enabled = enabled,
            AutomaticAnalysisEnabled = automatic,
            MaximumConcurrency = concurrency,
            DailyTenantCostBudget = dailyBudget,
            MaximumProviderAttemptCost = attemptCost,
            DailyTenantLimit = tenantLimit,
            TenantRequestsPerMinute = rateLimit,
            MaximumTenantProviderConcurrency = tenantConcurrency,
            MaximumGlobalProviderConcurrency = globalConcurrency,
            BudgetCurrency = currency,
            ProviderEgressEnabled = egressEnabled,
            Provider = provider,
            RegionalProcessingApproved = regionalApproval,
            ProcessingRegion = region,
            ApprovedRegions = approvedRegions ?? ["eu", "us"],
            ApprovedTenantIds = [tenant],
            AllowedDataCategories = categories ?? ["logs", "metrics", "traceCorrelation"],
            Endpoint = endpoint,
            ResultRetentionDays = retentionDays,
            DailyProjectLimit = dailyLimit,
            Model = model
        };
    }


    /// <summary>
    ///     Reloadable synthetic options used to test operator kill switches without deployed
    ///     configuration.
    /// </summary>
    /// <param name="value">Synthetic analysis options instance.</param>
    internal sealed class Monitor(AiAnalysisOptions value) : IOptionsMonitor<AiAnalysisOptions>
    {
        /// <summary>Current test policy snapshot.</summary>
        public AiAnalysisOptions CurrentValue { get; set; } = value;

        /// <summary>
        ///     Returns current synthetic options without selecting by option name.
        /// </summary>
        /// <param name="name">Fixture member, option or metric name selected by the caller; ignored by this fixture.</param>
        /// <returns>The current synthetic options instance.</returns>
        public AiAnalysisOptions Get(string? name)
        {
            return CurrentValue;
        }

        /// <summary>
        ///     Returns no change subscription because this options fixture does not notify listeners.
        /// </summary>
        /// <param name="listener">Options-change callback supplied by the caller; ignored by this fixture.</param>
        /// <returns>Null; this fixture does not subscribe to options changes.</returns>
        public IDisposable? OnChange(Action<AiAnalysisOptions, string?> listener)
        {
            return null;
        }
    }

    /// <summary>
    ///     Provider fixture authorization is explicit and never used by production registration.
    /// </summary>
    /// <param name="allowed">Exact selected evidence references; null leaves membership validation to the caller.</param>
    /// <param name="onCheck">Optional callback invoked during the authorization probe.</param>
    internal sealed class Authorizer(bool allowed = true, Action? onCheck = null) : IAnalysisEgressAuthorizer
    {
        /// <summary>
        ///     Checks caller cancellation, invokes the optional probe and applies configured fixture
        ///     authorization.
        /// </summary>
        /// <param name="deploymentId">Synthetic deployment identity.</param>
        /// <param name="trigger">
        ///     The manual or automatic trigger; it does not grant provider egress permission; ignored by
        ///     this fixture.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the configured fixture condition or completion outcome.</returns>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public Task<bool> AuthorizeAsync(Guid deploymentId, AiAnalysisTrigger trigger,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onCheck?.Invoke();

            return Task.FromResult(allowed && deploymentId != Guid.Empty);
        }
    }
}
