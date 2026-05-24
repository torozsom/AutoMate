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


    /// <summary>
    ///     Initializes the component and populates the UI list of environment variables
    ///     from the incoming configuration dictionary.
    /// </summary>
    protected override void OnInitialized()
    {
        Config.Databases ??= [];

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
        Config.CustomEnvVars ??= new Dictionary<string, string>();
        Config.CustomEnvVars.Clear();

        // Ignore completely empty keys to prevent dictionary crash or invalid configurations
        foreach (var item in _envVars)
            if (!string.IsNullOrWhiteSpace(item.Key))
                // Clean the key from accidential whitespaces
                Config.CustomEnvVars.TryAdd(item.Key.Trim(), item.Value?.Trim() ?? string.Empty);

        await OnDeployConfirmed.InvokeAsync(Config);
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
            DbUser = "admin",
            DbPassword = "AdminPwd123",
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
}
