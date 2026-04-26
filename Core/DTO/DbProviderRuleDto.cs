namespace Core.DTO;

/// <summary>
///     Represents a database provider rule, which includes the type of the database provider and a list of associated
///     package names.
/// </summary>
public record DbProviderRuleDto
{
    /// <summary>
    ///     The type of the database provider (e.g., "SqlServer", "PostgreSql", "MySql").
    /// </summary>
    public string DbType { get; set; } = string.Empty;

    /// <summary>
    ///     A list of package names that are associated with the database provider.
    /// </summary>
    public List<string> Packages { get; set; } = [];
}