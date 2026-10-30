using Domain.Entities.Users;

namespace Domain.Entities.Ai;

/// <summary>
///     Durable admission or conservative attempt charge independent of project, deployment and
///     result deletion.
/// </summary>
public sealed class AiAnalysisBudgetEntry : BaseEntity
{
    /// <summary>Owner-account tenant whose quota or budget is charged.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Account deletion removes its budget metadata; project/result deletion does not.</summary>
    public User Tenant { get; init; } = null!;

    /// <summary>Analysis identity without a cascading result foreign key.</summary>
    public Guid AnalysisId { get; init; }

    /// <summary>False records one new admission; true records one reserved provider attempt.</summary>
    public bool IsProviderAttempt { get; init; }

    /// <summary>Provider attempt ownership token; null for admission entries.</summary>
    public Guid? LeaseId { get; init; }

    /// <summary>UTC accounting day, independent of result timestamps and retry history.</summary>
    public DateOnly AccountingDay { get; init; }

    /// <summary>
    ///     Explicit accounting-clock UTC timestamp for rolling limits and retention; audit
    ///     timestamps are independent.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    ///     Conservative non-refundable amount in units of 1/100,000,000 currency; zero for
    ///     admission.
    /// </summary>
    public long ReservedCostUnits { get; init; }

    /// <summary>Three-letter reservation currency, not a provider billing assertion.</summary>
    public string Currency { get; init; } = "USD";
}
