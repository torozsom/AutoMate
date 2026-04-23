namespace Core.Entities;

public abstract class BaseEntity
{
    /// <summary>
    ///     Gets or sets the unique identifier for the entity. This is a GUID that is automatically generated when a new
    ///     instance of the entity is created.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     Gets or sets the timestamp when the entity was created. This is automatically set to the current UTC time when a
    ///     new instance of the entity is created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp when the entity was last updated. This is automatically updated to the current UTC time
    ///     whenever the entity is modified.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}