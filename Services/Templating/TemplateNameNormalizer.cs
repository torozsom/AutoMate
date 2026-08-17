namespace Services.Templating;

/// <summary>
///     Normalizes names used in generated Docker, Azure, and Container Apps resources.
/// </summary>
internal static class TemplateNameNormalizer
{
    /// <summary>
    ///     Creates a conservative Azure- and image-friendly name from user-supplied project names.
    /// </summary>
    public static string NormalizeResourceName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);

        normalized = normalized.Trim('-');

        return string.IsNullOrWhiteSpace(normalized) ? "automate-app" : normalized;
    }

    /// <summary>
    ///     Normalizes database aliases to the canonical names expected by templates.
    /// </summary>
    public static string NormalizeDatabaseType(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => "PostgreSQL",
            "mysql" => "MySQL",
            "sqlserver" or "sql-server" or "mssql" or "microsoft sql server" => "SQLServer",
            "mongodb" or "mongo" => "MongoDB",
            "redis" => "Redis",
            _ => value.Trim()
        };
    }

    /// <summary>
    ///     Indicates whether a database type needs generated username and password parameters.
    /// </summary>
    public static bool RequiresDatabaseLogin(string databaseType)
    {
        return databaseType is "PostgreSQL" or "MySQL" or "SQLServer";
    }

    /// <summary>
    ///     Creates a resource name segment capped to the requested length.
    /// </summary>
    public static string NormalizeResourceSegment(string value, int maxLength)
    {
        var normalized = NormalizeResourceName(value);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd('-');
    }

    /// <summary>
    ///     Creates an Azure Container Apps secret name capped to Azure's 63-character limit.
    /// </summary>
    public static string NormalizeContainerAppSecretName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "automate-secret";

        return normalized.Length <= 63 ? normalized : normalized[..63].TrimEnd('-');
    }
}