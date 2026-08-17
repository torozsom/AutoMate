namespace Core.DTO;

/// <summary>
///     Represents a database provider detection rule loaded from the scanner rule catalog.
/// </summary>
public record DbProviderRuleDto
{
    /// <summary>
    ///     The database provider type to apply when a matching package is detected.
    /// </summary>
    public string DbType { get; init; } = string.Empty;

    /// <summary>
    ///     The package-name fragments that identify this provider.
    /// </summary>
    public List<string> Packages { get; init; } = [];
}