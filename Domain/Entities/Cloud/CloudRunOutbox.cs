namespace Domain.Entities.Cloud;

/// <summary>
///     Transactional wakeup for an admitted cloud run.
/// </summary>
public sealed class CloudRunOutbox : BaseEntity
{
    /// <summary>Run to make eligible for a worker.</summary>
    public Guid RunId { get; init; }

    /// <summary>Time the durable scheduler consumed this wakeup.</summary>
    public DateTimeOffset? DispatchedAt { get; set; }
}
