namespace Core.Entities;

/// <summary>
///     Represents the shared persistence identity and audit fields for domain entities.
/// </summary>
public abstract class BaseEntity
{
    /// <summary>
    ///     Gets or sets the unique identifier for the entity.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     Gets or sets the timestamp when the entity was created. The data layer populates this value on insert.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp when the entity was last updated. The data layer refreshes this value on save.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}