using Core.Defaults;
using Core.DTO;

namespace Services.Orchestration;

/// <summary>
///     Applies default Azure resource names for cloud deployment configuration.
/// </summary>
internal static class CloudDeploymentDefaults
{
    /// <summary>
    ///     Applies default values to missing cloud configuration fields.
    /// </summary>
    public static void Apply(DeploymentConfigDto config)
    {
        var resourceName = OrchestrationNameNormalizer.NormalizeResourceName(config.ProjectName);
        var environmentSuffix = GetEnvironmentSuffix(config.EnvironmentName);
        var baseName = $"{resourceName}-{environmentSuffix}";

        if (string.IsNullOrWhiteSpace(config.CloudAzureRegion))
            config.CloudAzureRegion = DeploymentDefaults.AzureRegion;

        if (string.IsNullOrWhiteSpace(config.CloudResourceGroupName))
            config.CloudResourceGroupName = $"{baseName}-rg";

        if (string.IsNullOrWhiteSpace(config.CloudContainerAppName))
            config.CloudContainerAppName = $"{baseName}-app";

        if (string.IsNullOrWhiteSpace(config.CloudRegistryName))
            config.CloudRegistryName = "ghcr.io";
    }

    /// <summary>
    ///     Creates the suffix used in default cloud resource names from the environment name.
    /// </summary>
    private static string GetEnvironmentSuffix(string environmentName)
    {
        var normalized = environmentName.Trim().ToLowerInvariant();

        return normalized switch
        {
            "production" => "prod",
            "staging" => "stg",
            "development" => "dev",
            _ when normalized.Length > 0 => OrchestrationNameNormalizer.NormalizeResourceName(normalized),
            _ => "dev"
        };
    }
}