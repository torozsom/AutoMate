using Core.DTO;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Pages;

/// <summary>
///     A Blazor component that provides a user interface for configuring deployment settings for a .NET project.
/// </summary>
public partial class ConfigurationForm : ComponentBase
{
    /// A counter used to generate unique keys for new environment variables added by the user.
    private int _newVarCounter = 1;

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
    ///     Invoked when the user confirms the deployment configuration. This method triggers the OnDeployConfirmed event
    ///     callback,
    ///     passing the current deployment configuration (Config) to the parent component or service that will handle the
    ///     deployment process.
    /// </summary>
    private async Task ConfirmDeploy()
    {
        await OnDeployConfirmed.InvokeAsync(Config);
    }


    /// <summary>
    /// </summary>
    private void AddEmptyEnvVar()
    {
        var newKey = $"NEW_VAR_{_newVarCounter++}";
        Config.CustomEnvVars.TryAdd(newKey, "");
    }


    /// <summary>
    ///     Removes an environment variable from the deployment configuration based on the specified key.
    /// </summary>
    /// <param name="key">The key of the environment variable to be removed.</param>
    private void RemoveEnvVar(string key)
    {
        Config.CustomEnvVars.Remove(key);
    }


    /// <summary>
    ///     Updates the key of an existing environment variable in the deployment configuration.
    /// </summary>
    /// <param name="oldKey">The old key of the variable to be updated.</param>
    /// <param name="newKey">The new key to be set for the specific variable.</param>
    private void UpdateEnvVarKey(string oldKey, string? newKey)
    {
        if (string.IsNullOrWhiteSpace(newKey) || newKey == oldKey || Config.CustomEnvVars.ContainsKey(newKey))
            return;

        var value = Config.CustomEnvVars[oldKey];
        Config.CustomEnvVars.Remove(oldKey);
        Config.CustomEnvVars.Add(newKey, value);
    }


    /// <summary>
    ///     Updates the value of an existing environment variable in the deployment configuration based on the specified key.
    /// </summary>
    /// <param name="key">The key of the variable to be updated.</param>
    /// <param name="newValue">The new value to be set for the variable.</param>
    private void UpdateEnvVarValue(string key, string? newValue)
    {
        if (Config.CustomEnvVars.ContainsKey(key))
            Config.CustomEnvVars[key] = newValue ?? string.Empty;
    }
}