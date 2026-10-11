namespace Domain.Entities;

/// <summary>Durable deletion work that survives deletion of its project or owner.</summary>
public sealed class DeploymentArchiveCleanup : BaseEntity
{
    /// <summary>Former owner used to locate the archived partition.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Deleted project whose archive must be removed.</summary>
    public Guid ProjectId { get; set; }
}