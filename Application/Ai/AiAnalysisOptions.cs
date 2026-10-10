namespace Application.Ai;

public sealed class AiAnalysisOptions
{
    public const string SectionName = "AiAnalysis";

    /// <summary>Upper bound on per-instance processing slots to prevent unbounded worker fan-out.</summary>
    public const int MaximumSupportedConcurrency = 16;

    /// <summary>Global analysis feature flag; default off independently of deployment execution.</summary>
    public bool Enabled { get; init; }

    /// <summary>Additional opt-in for failure-triggered analysis within the approved owner cohort.</summary>
    public bool AutomaticAnalysisEnabled { get; init; }

    /// <summary>Independent operator kill switch; reload also cancels active OpenAI requests locally.</summary>
    public bool ProviderEgressEnabled { get; init; }

    /// <summary>Canonical registered Infrastructure adapter identifier; selection never implicitly approves egress.</summary>
    public string? Provider { get; init; }

    /// <summary>Operator attestation that contracts, account/project and model support the selected regional processing.</summary>
    public bool RegionalProcessingApproved { get; init; }

    /// <summary>Requested processing geography; initially only us and eu are supported.</summary>
    public string? ProcessingRegion { get; init; }

    /// <summary>Operator-approved processing geographies; an empty list denies transmission.</summary>
    public string[] ApprovedRegions { get; init; } = [];

    /// <summary>Approved owner-account identifiers, the current tenant boundary in both hosting profiles.</summary>
    public Guid[] ApprovedTenantIds { get; init; } = [];

    /// <summary>Explicit data approvals; the current context requires logs, metrics and traceCorrelation together.</summary>
    public string[] AllowedDataCategories { get; init; } = [];

    public string Endpoint { get; init; } = "https://api.openai.com/v1/";
    public string Model { get; init; } = "gpt-5-mini";
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Provider output-token allowance, including reasoning, from one to the existing 8,192-token ceiling.</summary>
    public int MaximumOutputTokens { get; init; } = 8192;

    public int MaximumContextCharacters { get; init; } = 24_000;

    /// <summary>Maximum UTF-8 bytes of the JSON-escaped provider context input.</summary>
    public int MaximumContextBytes { get; init; } = 48_000;

    /// <summary>Conservative context token bound charged at one token per encoded UTF-8 byte.</summary>
    public int MaximumContextTokens { get; init; } = 12_000;

    /// <summary>Concurrent leased analyses per application instance, from one to sixteen; changes require restart.</summary>
    public int MaximumConcurrency { get; init; } = 1;

    /// <summary>Renewable queue lease lifetime, between 30 and 900 seconds; requires synchronized worker clocks.</summary>
    public int LeaseDurationSeconds { get; init; } = 120;

    /// <summary>Maximum processing acquisitions before an additional claim terminally records interrupted recovery exhaustion.</summary>
    public int MaximumRecoveryAttempts { get; init; } = 3;

    /// <summary>Number of durable transient provider retries after the initial call, between zero and five.</summary>
    public int MaximumProviderRetries { get; init; } = 2;

    /// <summary>Initial exponential retry delay, between one and three hundred seconds and no greater than the maximum.</summary>
    public int RetryBaseDelaySeconds { get; init; } = 10;

    /// <summary>Maximum retry delay, between five and 3,600 seconds; longer server hints stop retries.</summary>
    public int RetryMaximumDelaySeconds { get; init; } = 300;

    /// <summary>New analyses admitted per UTC project day, between zero and 1,000; result deletion does not refund usage.</summary>
    public int DailyProjectLimit { get; init; } = 5;

    /// <summary>New analyses per owner-account tenant across all projects in a UTC day; zero denies admission.</summary>
    public int DailyTenantLimit { get; init; } = 100;

    /// <summary>New analyses per tenant in a rolling sixty-second window; replay and active aliases do not count.</summary>
    public int TenantRequestsPerMinute { get; init; } = 10;

    /// <summary>Concurrent provider attempts with live budget reservations per tenant across worker instances.</summary>
    public int MaximumTenantProviderConcurrency { get; init; } = 2;

    /// <summary>Concurrent provider attempts with live budget reservations across all tenants and instances.</summary>
    public int MaximumGlobalProviderConcurrency { get; init; } = 16;

    /// <summary>Daily reserved spend per tenant; zero defaults to denying provider execution.</summary>
    public decimal DailyTenantCostBudget { get; init; }

    /// <summary>Operator-approved worst-case cost of one bounded request, reserved before every attempt without refund.</summary>
    public decimal MaximumProviderAttemptCost { get; init; }

    /// <summary>Three-letter currency of reservation limits; changing currency during a charged UTC day denies execution.</summary>
    public string BudgetCurrency { get; init; } = "USD";

    /// <summary>Expiry for newly admitted result/work metadata, between one and ninety days; existing expiry is unchanged.</summary>
    public int ResultRetentionDays { get; init; } = 90;
}