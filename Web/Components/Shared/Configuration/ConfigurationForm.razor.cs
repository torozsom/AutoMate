using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Orchestration.Cloud;
using Domain.Defaults;
using Domain.DTO.Deployment;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared.Configuration;

/// <summary>
///     A Blazor component that provides a user interface for configuring deployment settings for a
///     .NET project.
/// </summary>
public partial class ConfigurationForm : ComponentBase
{
    /// <summary>UI-friendly representation of environment variables to allow Blazor data binding.</summary>
    private readonly List<EnvVarItem> _envVars = [];

    /// <summary>Last generated names used to preserve explicit edits.</summary>
    private CloudDefaults? _lastCloudDefaults;

    /// <summary>Prevents overlapping scans and exposes progress to the form.</summary>
    private bool _loadingVariables;

    /// <summary>Current deployment environment name.</summary>
    private string _selectedEnvironment = DeploymentDefaults.DevelopmentEnvironmentName;

    /// <summary>First actionable deployment-setting error.</summary>
    private string? _validationMessage;

    /// <summary>Safe feedback without configuration values or filesystem details.</summary>
    private string? _variableLoadMessage;

    /// <summary>Hosting mode determines the cloud registry guidance and defaults.</summary>
    [Inject]
    private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>Provides the public GitHub App installation URL in SaaS mode.</summary>
    [Inject]
    private IConfiguration Configuration { get; set; } = null!;

    /// <summary>Public installation URL for the configured AutoMate GitHub App.</summary>
    private string? GitHubAppInstallUrl => string.IsNullOrWhiteSpace(Configuration["GitHubApp:AppSlug"])
        ? null
        : $"https://github.com/apps/{Uri.EscapeDataString(Configuration["GitHubApp:AppSlug"]!)}/installations/new";


    /// <summary>The deployment configuration settings for the project being deployed.</summary>
    [Parameter]
    public DeploymentConfigDto Config { get; set; } = new();

    /// <summary>The file system path to the project being configured for deployment.</summary>
    [Parameter]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>Indicates whether the form should render cloud-specific deployment settings.</summary>
    [Parameter]
    public bool IsCloudDeployment { get; set; }

    /// <summary>An event callback invoked when the user confirms the deployment with the specified configuration.</summary>
    [Parameter]
    public EventCallback<DeploymentConfigDto> OnDeployConfirmed { get; set; }

    /// <summary>An event callback invoked when the user cancels the deployment configuration process.</summary>
    [Parameter]
    public EventCallback OnCancel { get; set; }


    /// <summary>Service responsible for scanning project files to extract environment variables and metadata.</summary>
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

    /// <summary>Updates environment defaults while preserving edited names.</summary>
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
    ///     Initializes the component and populates the UI list of environment variables from the incoming
    ///     configuration dictionary.
    /// </summary>
    protected override void OnInitialized()
    {
        if (Config is { Databases: null }) Config.Databases = [];
        Config.EnvironmentName = string.IsNullOrWhiteSpace(Config.EnvironmentName)
            ? DeploymentDefaults.DevelopmentEnvironmentName
            : Config.EnvironmentName;
        _selectedEnvironment = Config.EnvironmentName;

        if (IsCloudDeployment)
            ApplyCloudDefaults(true);

        if (Config is { CustomEnvVars: { } environment })
            foreach (var kvp in environment)
                _envVars.Add(new EnvVarItem { Key = kvp.Key, Value = kvp.Value });
    }


    /// <summary>
    ///     Scans the project directory for configuration files (appsettings.json, launchSettings.json,
    ///     .env) and loads discovered variables into the UI list without overwriting existing ones.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task LoadVariablesFromConfigFilesAsync()
    {
        if (IsCloudDeployment || _loadingVariables) return;
        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            _variableLoadMessage = "Project files are unavailable. Reopen deployment settings and try again.";
            return;
        }

        _loadingVariables = true;
        _variableLoadMessage = null;
        try
        {
            var scannedVars = await ProjectScanner.ExtractEnvironmentVariablesAsync(ProjectPath);
            var added = 0;
            foreach (var kvp in scannedVars)
                if (!_envVars.Exists(e => string.Equals(e.Key.Trim(), kvp.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    _envVars.Add(new EnvVarItem { Key = kvp.Key, Value = kvp.Value });
                    added++;
                }

            _variableLoadMessage = scannedVars.Count == 0
                ? "No environment variables were found in the project configuration files."
                : $"Loaded {added} new environment variables. Existing entries were preserved.";
        }
        catch (Exception)
        {
            _variableLoadMessage =
                "Unable to read project configuration files. Check that they are accessible and try again.";
        }
        finally
        {
            _loadingVariables = false;
        }
    }


    /// <summary>
    ///     Validates and rebuilds the configuration dictionary, then triggers the deployment confirmation
    ///     event.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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

        if (Config is { CustomEnvVars: null }) Config.CustomEnvVars = new Dictionary<string, string>();
        Config.CustomEnvVars.Clear();

        // Ignore completely empty keys to prevent dictionary crash or invalid configurations
        foreach (var item in _envVars)
            if (!string.IsNullOrWhiteSpace(item.Key))
                // Clean the key from accidential whitespaces
                Config.CustomEnvVars.TryAdd(item.Key.Trim(),
                    item is { Value: { } value } ? value.Trim() : string.Empty);

        await OnDeployConfirmed.InvokeAsync(Config);
    }


    /// <summary>
    ///     Applies the existing application validation policy and retains the first authored form error.
    /// </summary>
    /// <returns>Whether the cloud settings satisfy the existing form rules.</returns>
    private bool ValidateCloudRuntimeConfiguration()
    {
        var error = CloudRuntimeConfigurationValidation.Validate(Config, _envVars.Select(item => item.Key));
        if (error is null) return true;
        _validationMessage = error;
        return false;
    }

    /// <summary>
    ///     Updates empty or previously generated resource names while preserving explicit user edits.
    /// </summary>
    /// <param name="overwriteOnlyEmptyFields">Whether populated values must remain unchanged.</param>
    /// <param name="previousDefaults">Previously generated names, or null when no defaults have been applied.</param>
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


    /// <summary>
    ///     Decides whether an empty or unchanged generated field can receive a new default.
    /// </summary>
    /// <param name="currentValue">Current user-editable field value.</param>
    /// <param name="previousDefault">Earlier generated value, if available.</param>
    /// <param name="overwriteOnlyEmptyFields">Whether populated values must remain unchanged.</param>
    /// <returns>True for empty fields or, when permitted, fields matching the prior generated default.</returns>
    private static bool ShouldApplyCloudDefault(string currentValue, string? previousDefault,
        bool overwriteOnlyEmptyFields)
    {
        if (string.IsNullOrWhiteSpace(currentValue))
            return true;

        return !overwriteOnlyEmptyFields &&
               !string.IsNullOrWhiteSpace(previousDefault) &&
               string.Equals(currentValue, previousDefault, StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    ///     Derives Azure resource names and the hosting-profile registry default.
    /// </summary>
    /// <param name="projectName">Project name used to derive resource defaults.</param>
    /// <param name="environmentName">Selected deployment environment.</param>
    /// <returns>Generated resource-group, app and registry defaults for the selected profile.</returns>
    private CloudDefaults BuildCloudDefaults(string projectName, string environmentName)
    {
        var resourceName = NormalizeResourceName(projectName);
        var environmentSuffix = GetEnvironmentSuffix(environmentName);
        var baseName = $"{resourceName}-{environmentSuffix}";

        return new CloudDefaults(
            $"{baseName}-rg",
            $"{baseName}-app",
            DeploymentCapabilities.LocalDeploymentsEnabled ? "ghcr.io" : string.Empty);
    }


    /// <summary>
    ///     Maps standard environment names to short resource suffixes.
    /// </summary>
    /// <param name="environmentName">Selected deployment environment.</param>
    /// <returns>A normalized short suffix, defaulting to "dev" for an empty name.</returns>
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


    /// <summary>
    ///     Creates a lowercase hyphenated resource name bounded to 23 characters.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>A nonempty normalized name of at most 23 characters.</returns>
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
    ///     Adds a PostgreSQL configuration with editable database, account and connection-key defaults.
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
    ///     Adds an empty environment-variable row for editing.
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
        /// <summary>Environment-variable name edited in the form.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Environment-variable value; may contain secrets and must not enter logs.</summary>
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    ///     Groups generated resource names for comparison with later user edits.
    /// </summary>
    /// <param name="ResourceGroup">Generated Azure resource-group name.</param>
    /// <param name="ContainerApp">Generated Container App name.</param>
    /// <param name="RegistryServer">Default registry server, possibly empty for managed provisioning.</param>
    private sealed record CloudDefaults(string ResourceGroup, string ContainerApp, string RegistryServer);
}
