namespace Core.Defaults;

/// <summary>
///     Defines shared deployment contract defaults used by Core DTOs and callers that create deployment requests.
/// </summary>
public static class DeploymentDefaults
{
    /// <summary>
    ///     The default environment name for local deployments.
    /// </summary>
    public const string DevelopmentEnvironmentName = "Development";

    /// <summary>
    ///     The default environment name for cloud deployments.
    /// </summary>
    public const string ProductionEnvironmentName = "Production";

    /// <summary>
    ///     The default HTTP port exposed by generated local deployment configuration.
    /// </summary>
    public const int LocalExposedPort = 8080;

    /// <summary>
    ///     The default Azure region used when no region has been selected yet.
    /// </summary>
    public const string AzureRegion = "eastus";

    /// <summary>
    ///     The default generated database name for local template output.
    /// </summary>
    public const string DatabaseName = "appdb";

    /// <summary>
    ///     The default generated database user for local template output.
    /// </summary>
    public const string DatabaseUser = "admin";

    /// <summary>
    ///     The development/template database password used only for generated local Docker assets.
    /// </summary>
    public const string DatabasePassword = "AdminPwd123";

    /// <summary>
    ///     The default connection-string configuration key used by generated templates.
    /// </summary>
    public const string ConnectionStringName = "DefaultConnection";

    /// <summary>
    ///     The default suffix used when naming generated database containers.
    /// </summary>
    public const string DatabaseContainerNameSuffix = "db";

    /// <summary>
    ///     The default branch where AutoMate commits generated cloud deployment files.
    /// </summary>
    public const string CloudDeploymentBranchName = "automate/azure-deployment";

    /// <summary>
    ///     The default GitHub Actions workflow file generated and dispatched by AutoMate.
    /// </summary>
    public const string CloudWorkflowFileName = "deploy.yml";
}