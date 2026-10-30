namespace Domain.Entities.Deployments;

/// <summary>
///     Durable deletion work that survives deletion of its project or owner.
/// </summary>
public sealed class DeploymentArchiveCleanup : BaseEntity
{
    /// <summary>Former owner used to locate the archived partition.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Deleted project whose archive must be removed.</summary>
    public Guid ProjectId { get; init; }
}
