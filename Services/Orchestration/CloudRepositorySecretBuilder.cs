using System.Text;
using Core.DTO;
using Services.Templating;

namespace Services.Orchestration;

/// <summary>
///     Builds GitHub Actions repository secrets required by generated cloud deployment workflows.
/// </summary>
internal static class CloudRepositorySecretBuilder
{
    /// <summary>
    ///     Creates the complete secret dictionary for Azure identity, registry, database, and custom env values.
    /// </summary>
    public static Dictionary<string, string> Build(CloudDeploymentRequestDto request,
        AzureOidcSetupResultDto oidcSetup)
    {
        var secrets = new Dictionary<string, string>
        {
            ["AZURE_CLIENT_ID"] = oidcSetup.ClientId,
            ["AZURE_TENANT_ID"] = oidcSetup.TenantId,
            ["AZURE_SUBSCRIPTION_ID"] = oidcSetup.SubscriptionId,
            ["GHCR_PAT"] = string.IsNullOrWhiteSpace(request.GitHubContainerRegistryToken)
                ? request.GitHubAccessToken
                : request.GitHubContainerRegistryToken
        };

        AddDatabaseSecrets(secrets, request.Config.Databases);
        AddCustomEnvironmentSecrets(secrets, request.Config.CustomEnvVars);

        return secrets;
    }

    /// <summary>
    ///     Adds base64-encoded database login secrets for database types that require credentials.
    /// </summary>
    private static void AddDatabaseSecrets(Dictionary<string, string> secrets, IEnumerable<DatabaseConfigDto> databases)
    {
        foreach (var (database, index) in databases.Select((database, index) => (database, index)))
        {
            if (!RequiresDatabaseLogin(database.DbType))
                continue;

            secrets[CloudDeploymentSecretNames.GetDatabaseUsernameSecretName(index)] =
                Base64Encode(string.IsNullOrWhiteSpace(database.DbUser) ? "automateadmin" : database.DbUser.Trim());
            secrets[CloudDeploymentSecretNames.GetDatabasePasswordSecretName(index)] =
                Base64Encode(database.DbPassword ?? string.Empty);
        }
    }

    /// <summary>
    ///     Adds base64-encoded custom environment value secrets in stable key order.
    /// </summary>
    private static void AddCustomEnvironmentSecrets(Dictionary<string, string> secrets,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        foreach (var (envVar, index) in environmentVariables
                     .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                     .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                     .Select((envVar, index) => (envVar, index)))
            secrets[CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(index, envVar.Key.Trim())] =
                Base64Encode(envVar.Value ?? string.Empty);
    }

    /// <summary>
    ///     Encodes secret values because generated Bicep decodes them at deployment time.
    /// </summary>
    private static string Base64Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    ///     Indicates whether a database provider requires generated login credentials.
    /// </summary>
    private static bool RequiresDatabaseLogin(string databaseType)
    {
        return databaseType.Trim().ToLowerInvariant() is "postgresql" or "postgres" or "mysql" or "sqlserver"
            or "sql-server" or "mssql" or "microsoft sql server";
    }
}