using Domain.DTO.Deployment;

namespace Application.Orchestration.Cloud;

/// <summary>
///     Preserves cloud form validation rules and first-error precedence independently of rendering and provider
///     constraints.
/// </summary>
public static class CloudRuntimeConfigurationValidation
{
    /// <summary>
    ///     Returns the existing first database/environment error without mutating confirmed settings.
    /// </summary>
    /// <param name="config">The current user-editable cloud database settings.</param>
    /// <param name="environmentKeys">Form keys in their existing input order; empty keys are ignored.</param>
    /// <returns>The authored first validation error, or null when all existing rules pass.</returns>
    public static string? Validate(DeploymentConfigDto config, IEnumerable<string> environmentKeys)
    {
        var connectionStringEnvNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in config.Databases)
        {
            if (!IsSupportedDatabaseEngine(database.DbType))
                return $"Database engine '{database.DbType}' is not supported for Azure deployments.";

            if (string.IsNullOrWhiteSpace(database.ConnectionStringName) ||
                !IsValidConfigurationKeySegment(database.ConnectionStringName))
                return "Database connection string names may contain only letters, numbers, and underscores.";

            if (!connectionStringEnvNames.Add($"ConnectionStrings__{database.ConnectionStringName.Trim()}"))
                return $"Connection string name '{database.ConnectionStringName}' is used more than once.";

            if (!string.Equals(database.DbType, "Redis", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(database.DbName))
                return "Database name is required for every non-Redis database.";

            if (RequiresDatabaseLogin(database.DbType) && string.IsNullOrWhiteSpace(database.DbUser))
                return "PostgreSQL, MySQL, and SQL Server databases require an administrator username.";

            if (RequiresDatabaseLogin(database.DbType) && !IsValidDatabaseAdministrator(database.DbUser))
                return
                    "Database administrator usernames may contain only letters and numbers, must start with a letter, and cannot use common reserved names.";

            if (RequiresDatabaseLogin(database.DbType) && string.IsNullOrWhiteSpace(database.DbPassword))
                return "PostgreSQL, MySQL, and SQL Server databases require an administrator password.";

            if (RequiresDatabaseLogin(database.DbType) && !IsStrongEnoughDatabasePassword(database.DbPassword))
                return
                    "Database passwords must be at least 8 characters and include uppercase, lowercase, and numeric characters.";
        }

        var customEnvKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var environmentKey in environmentKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            var key = environmentKey.Trim();
            if (!IsValidEnvironmentVariableName(key))
                return
                    "Environment variable names may contain only letters, numbers, and underscores, and cannot start with a number.";

            if (!customEnvKeys.Add(key)) return $"Environment variable '{key}' is defined more than once.";

            if (connectionStringEnvNames.Contains(key))
                return $"Environment variable '{key}' is already generated from the database configuration.";
        }

        return null;
    }

    /// <summary>
    ///     Checks the finite catalog of supported database engine names.
    /// </summary>
    /// <param name="dbType">Configured database engine name.</param>
    /// <returns>True for PostgreSQL, MySQL, SQL Server, MongoDB or Redis after trimming and case normalization.</returns>
    private static bool IsSupportedDatabaseEngine(string dbType)
    {
        return dbType.Trim().ToLowerInvariant() switch
        {
            "postgresql" or "mysql" or "sqlserver" or "mongodb" or "redis" => true,
            _ => false
        };
    }


    /// <summary>
    ///     Checks whether the engine requires administrator credentials.
    /// </summary>
    /// <param name="dbType">Configured database engine name.</param>
    /// <returns>True for PostgreSQL, MySQL or SQL Server; otherwise false.</returns>
    private static bool RequiresDatabaseLogin(string dbType)
    {
        return dbType.Trim().ToLowerInvariant() is "postgresql" or "mysql" or "sqlserver";
    }


    /// <summary>
    ///     Checks a nonempty connection-key segment for letters, digits and underscores.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>True for a nonempty trimmed sequence of letters, digits and underscores.</returns>
    private static bool IsValidConfigurationKeySegment(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Trim().All(c => char.IsLetterOrDigit(c) || c == '_');
    }


    /// <summary>
    ///     Checks administrator syntax and rejects reserved account names.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>True for a nonreserved alphanumeric account name beginning with a letter.</returns>
    private static bool IsValidDatabaseAdministrator(string value)
    {
        var trimmed = value.Trim();
        string[] reservedNames = ["admin", "administrator", "root", "guest", "public", "sa"];

        return trimmed.Length > 0 &&
               char.IsLetter(trimmed[0]) &&
               trimmed.All(char.IsLetterOrDigit) &&
               !reservedNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }


    /// <summary>
    ///     Checks the minimum length and uppercase, lowercase and numeric requirements.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>True for at least eight characters containing uppercase, lowercase and numeric characters.</returns>
    private static bool IsStrongEnoughDatabasePassword(string value)
    {
        return value.Length >= 8 &&
               value.Any(char.IsUpper) &&
               value.Any(char.IsLower) &&
               value.Any(char.IsDigit);
    }


    /// <summary>
    ///     Checks the environment-variable identifier syntax.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>True for a nonempty letter/underscore-led name containing only letters, digits and underscores.</returns>
    private static bool IsValidEnvironmentVariableName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 &&
               (char.IsLetter(trimmed[0]) || trimmed[0] == '_') &&
               trimmed.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
