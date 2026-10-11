namespace Domain.Entities;

/// <summary>Durable admission or conservative attempt charge independent of project, deployment and result deletion.</summary>
public sealed class AiAnalysisBudgetEntry : BaseEntity
{
    /// <summary>Owner-account tenant whose quota or budget is charged.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Account deletion removes its budget metadata; project/result deletion does not.</summary>
    public User Tenant { get; set; } = null!;

    /// <summary>Analysis identity without a cascading result foreign key.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>False records one new admission; true records one reserved provider attempt.</summary>
    public bool IsProviderAttempt { get; set; }

    /// <summary>Provider attempt ownership token; null for admission entries.</summary>
    public Guid? LeaseId { get; set; }

    /// <summary>UTC accounting day, independent of result timestamps and retry history.</summary>
    public DateOnly AccountingDay { get; set; }

    /// <summary>Explicit accounting-clock UTC timestamp for rolling limits and retention; audit timestamps are independent.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Conservative non-refundable amount in units of 1/100,000,000 currency; zero for admission.</summary>
    public long ReservedCostUnits { get; set; }

    /// <summary>Three-letter reservation currency, not a provider billing assertion.</summary>
    public string Currency { get; set; } = "USD";
}