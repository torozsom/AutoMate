using Core.Defaults;
using Core.DTO;
using Microsoft.AspNetCore.Components;
using Services.Scanner;

namespace Web.Components.Shared;

/// <summary>
///     A Blazor component that provides a user interface for configuring deployment settings for a .NET project.
/// </summary>
public partial class ConfigurationForm : ComponentBase
{
    /// <summary>
    ///     UI-friendly representation of environment variables to allow Blazor data binding.
    /// </summary>
    private readonly List<EnvVarItem> _envVars = [];

    private CloudDefaults? _lastCloudDefaults;
    private string _selectedEnvironment = DeploymentDefaults.DevelopmentEnvironmentName;
    private string? _validationMessage;


    /// <summary>
    ///     The deployment configuration settings for the project being deployed.
    /// </summary>
    [Parameter]
    public DeploymentConfigDto Config { get; set; } = new();

    /// <summary>
    ///     The file system path to the project being configured for deployment.
    /// </summary>
    [Parameter]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>
    ///     Indicates whether the form should render cloud-specific deployment settings.
    /// </summary>
    [Parameter]
    public bool IsCloudDeployment { get; set; }

    /// <summary>
    ///     An event callback invoked when the user confirms the deployment with the specified configuration.
    /// </summary>
    [Parameter]
    public EventCallback<DeploymentConfigDto> OnDeployConfirmed { get; set; }

    /// <summary>
    ///     An event callback invoked when the user cancels the deployment configuration process.
    /// </summary>
    [Parameter]
    public EventCallback OnCancel { get; set; }


    /// <summary>
    ///     Service responsible for scanning project files to extract environment variables and metadata.
    /// </summary>
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

    private string SelectedEnvironment
    {
        get => _selectedEnvironment;
        set
        {
            if (string.Equals(_selectedEnvironment, value, StringComparison.Ordinal))
                return;

            var previousDefaults = _lastCloudDefaults ?? BuildCloudDefaults(Config.ProjectName, _selectedEnvironment);
            _selectedEnvironment = value;
            Config.EnvironmentName = value;

            if (IsCloudDeployment)
                ApplyCloudDefaults(false, previousDefaults);
        }
    }


    /// <summary>
    ///     Initializes the component and populates the UI list of environment variables
    ///     from the incoming configuration dictionary.
    /// </summary>
    protected override void OnInitialized()
    {
        Config.Databases ??= [];
        Config.EnvironmentName = string.IsNullOrWhiteSpace(Config.EnvironmentName)
            ? DeploymentDefaults.DevelopmentEnvironmentName
            : Config.EnvironmentName;
        _selectedEnvironment = Config.EnvironmentName;

        if (IsCloudDeployment)
            ApplyCloudDefaults(true);

        if (Config.CustomEnvVars is not null)
            foreach (var kvp in Config.CustomEnvVars)
                _envVars.Add(new EnvVarItem { Key = kvp.Key, Value = kvp.Value });
    }


    /// <summary>
    ///     Scans the project directory for configuration files (appsettings.json, launchSettings.json, .env)
    ///     and loads discovered variables into the UI list without overwriting existing ones.
    /// </summary>
    private async Task LoadVariablesFromConfigFilesAsync()
    {
        if (IsCloudDeployment || string.IsNullOrWhiteSpace(ProjectPath)) return;

        // Analyze the project files and extract environment variables.
        var scannedVars = await ProjectScanner.ExtractEnvironmentVariablesAsync(ProjectPath);

        // Only add variables that are not already present in our UI list
        foreach (var kvp in scannedVars)
            if (!_envVars.Exists(e => e.Key.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)))
                _envVars.Add(new EnvVarItem { Key = kvp.Key, Value = kvp.Value });
    }


    /// <summary>
    ///     Validates and rebuilds the configuration dictionary, then triggers the deployment confirmation event.
    /// </summary>
    private async Task ConfirmDeploy()
    {
        _validationMessage = null;

        if (!IsCloudDeployment && Config.ExposedPort is < 1 or > 65535)
        {
            _validationMessage = "Choose a valid host port between 1 and 65535.";
            return;
        }

        if (IsCloudDeployment && !ValidateCloudRuntimeConfiguration())
            return;

        Config.CustomEnvVars ??= new Dictionary<string, string>();
        Config.CustomEnvVars.Clear();

        // Ignore completely empty keys to prevent dictionary crash or invalid configurations
        foreach (var item in _envVars)
            if (!string.IsNullOrWhiteSpace(item.Key))
                // Clean the key from accidential whitespaces
                Config.CustomEnvVars.TryAdd(item.Key.Trim(), item.Value?.Trim() ?? string.Empty);

        await OnDeployConfirmed.InvokeAsync(Config);
    }


    private bool ValidateCloudRuntimeConfiguration()
    {
        var connectionStringEnvNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in Config.Databases)
        {
            if (!IsSupportedDatabaseEngine(database.DbType))
            {
                _validationMessage =
                    $"Database engine '{database.DbType}' is not supported for Azure deployments.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(database.ConnectionStringName) ||
                !IsValidConfigurationKeySegment(database.ConnectionStringName))
            {
                _validationMessage =
                    "Database connection string names may contain only letters, numbers, and underscores.";
                return false;
            }

            if (!connectionStringEnvNames.Add($"ConnectionStrings__{database.ConnectionStringName.Trim()}"))
            {
                _validationMessage =
                    $"Connection string name '{database.ConnectionStringName}' is used more than once.";
                return false;
            }

            if (!string.Equals(database.DbType, "Redis", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(database.DbName))
            {
                _validationMessage = "Database name is required for every non-Redis database.";
                return false;
            }

            if (RequiresDatabaseLogin(database.DbType) && string.IsNullOrWhiteSpace(database.DbUser))
            {
                _validationMessage = "PostgreSQL, MySQL, and SQL Server databases require an administrator username.";
                return false;
            }

            if (RequiresDatabaseLogin(database.DbType) && !IsValidDatabaseAdministrator(database.DbUser))
            {
                _validationMessage =
                    "Database administrator usernames may contain only letters and numbers, must start with a letter, and cannot use common reserved names.";
                return false;
            }

            if (RequiresDatabaseLogin(database.DbType) && string.IsNullOrWhiteSpace(database.DbPassword))
            {
                _validationMessage = "PostgreSQL, MySQL, and SQL Server databases require an administrator password.";
                return false;
            }

            if (RequiresDatabaseLogin(database.DbType) && !IsStrongEnoughDatabasePassword(database.DbPassword))
            {
                _validationMessage =
                    "Database passwords must be at least 8 characters and include uppercase, lowercase, and numeric characters.";
                return false;
            }
        }

        var customEnvKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var envVar in _envVars.Where(envVar => !string.IsNullOrWhiteSpace(envVar.Key)))
        {
            var key = envVar.Key.Trim();
            if (!IsValidEnvironmentVariableName(key))
            {
                _validationMessage =
                    "Environment variable names may contain only letters, numbers, and underscores, and cannot start with a number.";
                return false;
            }

            if (!customEnvKeys.Add(key))
            {
                _validationMessage = $"Environment variable '{key}' is defined more than once.";
                return false;
            }

            if (connectionStringEnvNames.Contains(key))
            {
                _validationMessage =
                    $"Environment variable '{key}' is already generated from the database configuration.";
                return false;
            }
        }

        return true;
    }


    private static bool IsSupportedDatabaseEngine(string dbType)
    {
        return dbType.Trim().ToLowerInvariant() switch
        {
            "postgresql" or "mysql" or "sqlserver" or "mongodb" or "redis" => true,
            _ => false
        };
    }


    private static bool RequiresDatabaseLogin(string dbType)
    {
        return dbType.Trim().ToLowerInvariant() is "postgresql" or "mysql" or "sqlserver";
    }


    private static bool IsValidConfigurationKeySegment(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Trim().All(c => char.IsLetterOrDigit(c) || c == '_');
    }


    private static bool IsValidDatabaseAdministrator(string value)
    {
        var trimmed = value.Trim();
        string[] reservedNames = ["admin", "administrator", "root", "guest", "public", "sa"];

        return trimmed.Length > 0 &&
               char.IsLetter(trimmed[0]) &&
               trimmed.All(char.IsLetterOrDigit) &&
               !reservedNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }


    private static bool IsStrongEnoughDatabasePassword(string value)
    {
        return value.Length >= 8 &&
               value.Any(char.IsUpper) &&
               value.Any(char.IsLower) &&
               value.Any(char.IsDigit);
    }


    private static bool IsValidEnvironmentVariableName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 &&
               (char.IsLetter(trimmed[0]) || trimmed[0] == '_') &&
               trimmed.All(c => char.IsLetterOrDigit(c) || c == '_');
    }


    private void ApplyCloudDefaults(bool overwriteOnlyEmptyFields, CloudDefaults? previousDefaults = null)
    {
        var defaults = BuildCloudDefaults(Config.ProjectName, Config.EnvironmentName);

        if (ShouldApplyCloudDefault(Config.CloudResourceGroupName, previousDefaults?.ResourceGroup,
                overwriteOnlyEmptyFields))
            Config.CloudResourceGroupName = defaults.ResourceGroup;

        if (ShouldApplyCloudDefault(Config.CloudContainerAppName, previousDefaults?.ContainerApp,
                overwriteOnlyEmptyFields))
            Config.CloudContainerAppName = defaults.ContainerApp;

        if (ShouldApplyCloudDefault(Config.CloudRegistryName, previousDefaults?.RegistryServer,
                overwriteOnlyEmptyFields))
            Config.CloudRegistryName = defaults.RegistryServer;

        _lastCloudDefaults = defaults;
    }


    private static bool ShouldApplyCloudDefault(string currentValue, string? previousDefault,
        bool overwriteOnlyEmptyFields)
    {
        if (string.IsNullOrWhiteSpace(currentValue))
            return true;

        return !overwriteOnlyEmptyFields &&
               !string.IsNullOrWhiteSpace(previousDefault) &&
               string.Equals(currentValue, previousDefault, StringComparison.OrdinalIgnoreCase);
    }


    private static CloudDefaults BuildCloudDefaults(string projectName, string environmentName)
    {
        var resourceName = NormalizeResourceName(projectName);
        var environmentSuffix = GetEnvironmentSuffix(environmentName);
        var baseName = $"{resourceName}-{environmentSuffix}";

        return new CloudDefaults(
            $"{baseName}-rg",
            $"{baseName}-app",
            "ghcr.io");
    }


    private static string GetEnvironmentSuffix(string environmentName)
    {
        var normalized = environmentName.Trim().ToLowerInvariant();

        return normalized switch
        {
            "production" => "prod",
            "staging" => "stg",
            "development" => "dev",
            _ when normalized.Length > 0 => NormalizeResourceName(normalized),
            _ => "dev"
        };
    }


    private static string NormalizeResourceName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "automate-app";

        return normalized.Length <= 23 ? normalized : normalized[..23].TrimEnd('-');
    }


    /// <summary>
    ///     Adds a new empty database configuration entry to the UI list.
    /// </summary>
    private void AddEmptyDatabase()
    {
        var dbCount = Config.Databases.Count + 1;
        Config.Databases.Add(new DatabaseConfigDto
        {
            DbType = "PostgreSQL",
            DbName = $"appdb{dbCount}",
            DbUser = "automateadmin",
            DbPassword = DeploymentDefaults.DatabasePassword,
            ConnectionStringName = $"Database{dbCount}Connection",
            ContainerNameSuffix = $"db{dbCount}"
        });
    }


    /// <summary>
    ///     Removes a database configuration entry from the UI list.
    /// </summary>
    /// <param name="db">The db to be removed.</param>
    private void RemoveDatabase(DatabaseConfigDto db)
    {
        Config.Databases.Remove(db);
    }


    /// <summary>
    ///     Adds a new empty environment variable entry to the UI list.
    /// </summary>
    private void AddEmptyEnvVar()
    {
        _envVars.Add(new EnvVarItem { Key = string.Empty, Value = string.Empty });
    }


    /// <summary>
    ///     Removes an environment variable entry from the UI list.
    /// </summary>
    /// <param name="item">The item to be removed.</param>
    private void RemoveEnvVar(EnvVarItem item)
    {
        _envVars.Remove(item);
    }


    /// <summary>
    ///     Inner class used solely for UI data binding of Key-Value pairs.
    /// </summary>
    private class EnvVarItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed record CloudDefaults(string ResourceGroup, string ContainerApp, string RegistryServer);
}