using Core.DTO;
using Microsoft.AspNetCore.Components;
using Services.Scanner;

namespace Web.Components.Pages;

/// <summary>
///     A Blazor component that provides a user interface for configuring deployment settings for a .NET project.
/// </summary>
public partial class ConfigurationForm : ComponentBase
{
    /// UI-friendly representation of environment variables.
    private readonly List<EnvVarItem> _envVars = [];

    /// <summary>
    ///     The deployment configuration settings for the project being deployed, including project details,
    ///     environment settings, database configuration, and custom environment variables.
    /// </summary>
    [Parameter]
    public DeploymentConfigDto Config { get; set; } = new();

    /// <summary>
    ///     An event callback that is invoked when the user confirms the deployment with the specified configuration.
    /// </summary>
    [Parameter]
    public EventCallback<DeploymentConfigDto> OnDeployConfirmed { get; set; }

    /// <summary>
    ///     An event callback that is invoked when the user cancels the deployment configuration process.
    /// </summary>
    [Parameter]
    public EventCallback OnCancel { get; set; }

    /// <summary>
    ///     The file system path to the project being configured for deployment.
    /// </summary>
    [Parameter]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>
    ///     A service responsible for scanning project files to extract environment variables and other metadata.
    /// </summary>
    [Inject]
    public IProjectScannerService ProjectScanner { get; set; } = default!;


    /// <summary>
    ///     On component initialization, we populate the UI list of
    ///     environment variables from the incoming configuration dictionary.
    /// </summary>
    protected override void OnInitialized()
    {
        // Populate the UI list from the incoming configuration dictionary
        if (Config.CustomEnvVars != null)
            foreach (var kvp in Config.CustomEnvVars)
                _envVars.Add(new EnvVarItem { Key = kvp.Key, Value = kvp.Value });
    }


    /// <summary>
    ///     Scans the project directory for configuration files (appsettings.json, launchSettings.json, .env)
    ///     and loads discovered variables into the CustomEnvVars dictionary without overwriting existing ones.
    /// </summary>
    private async Task LoadVariablesFromConfigFilesAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
            return;

        // Analyze the project files and extract environment variables.
        var scannedVars = await ProjectScanner.ExtractEnvironmentVariablesAsync(ProjectPath);

        // Only add variables that are not already present in our UI list
        foreach (var kvp in scannedVars)
            if (!_envVars.Any(e => e.Key.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)))
                _envVars.Add(new EnvVarItem { Key = kvp.Key, Value = kvp.Value });

        StateHasChanged();
    }


    /// <summary>
    ///     Invoked when the user confirms the deployment configuration. This method triggers the OnDeployConfirmed event
    ///     callback,
    ///     passing the current deployment configuration (Config) to the parent component or service that will handle the
    ///     deployment process.
    /// </summary>
    private async Task ConfirmDeploy()
    {
        // Rebuild the final Dictionary from the UI list before sending it back
        Config.CustomEnvVars.Clear();

        // Ignore completely empty keys to prevent dictionary crash or invalid configurations
        foreach (var item in _envVars)
            if (!string.IsNullOrWhiteSpace(item.Key))
                Config.CustomEnvVars.TryAdd(item.Key.Trim(), item.Value?.Trim() ?? string.Empty);

        await OnDeployConfirmed.InvokeAsync(Config);
    }


    /// <summary>
    ///     Adds a new empty environment variable entry to the UI list.
    ///     This allows the user to input a new key-value pair for environment variables.
    /// </summary>
    private void AddEmptyEnvVar()
    {
        _envVars.Add(new EnvVarItem { Key = "", Value = "" });
    }


    /// <summary>
    ///     Removes an environment variable entry from the UI list. This method is called when the user clicks the "Remove"
    ///     button
    ///     next to an environment variable entry, allowing them to delete unwanted variables from the configuration.
    /// </summary>
    /// <param name="item">The item to be removed.</param>
    private void RemoveEnvVar(EnvVarItem item)
    {
        _envVars.Remove(item);
    }


    /// <summary>
    ///     Inner class used solely for UI data binding.
    /// </summary>
    private class EnvVarItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}