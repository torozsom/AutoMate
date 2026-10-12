using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Scanner;

/// <summary>
///     Extracts environment variables from common .NET project configuration files.
/// </summary>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class ProjectEnvironmentVariableExtractor(ILogger logger)
{
    /// <summary>
    ///     Environment keys controlled by AutoMate's environment dropdown rather than extracted
    ///     variables.
    /// </summary>
    private static readonly string[] ManagedEnvironmentKeys =
    [
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT"
    ];

    /// <summary>
    ///     Extracts merged variables from appsettings, .env, and launchSettings files.
    /// </summary>
    /// <param name="projectPath">The local project path whose configuration files are scanned.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the extract async selection with its documented bounds and ordering.</returns>
    public async Task<Dictionary<string, string>> ExtractAsync(string projectPath, CancellationToken cancellationToken)
    {
        var extractedVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var appSettings = await ScanConfigurationFilesAsync(projectPath, cancellationToken);
            var launchData = await ScanLaunchSettingsAsync(projectPath, cancellationToken);
            var dotEnvData = await ScanDotEnvFilesAsync(projectPath, cancellationToken);

            foreach (var kvp in appSettings)
                extractedVars[kvp.Key] = kvp.Value;

            foreach (var kvp in dotEnvData)
                extractedVars[kvp.Key] = kvp.Value;

            foreach (var kvp in launchData.EnvVars)
                extractedVars[kvp.Key] = kvp.Value;

            foreach (var key in ManagedEnvironmentKeys)
                extractedVars.Remove(key);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                "[ProjectScannerService] Environment variable extraction cancelled for project '{ProjectPath}'." +
                "Ex: {ExceptionMessage}", projectPath, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[ProjectScannerService] Error extracting environment variables from config files at: {ProjectPath}",
                projectPath);
            throw;
        }

        return extractedVars;
    }

    /// <summary>
    ///     Reads appsettings*.json files and flattens JSON paths into environment variable style
    ///     keys.
    /// </summary>
    /// <param name="projectPath">The local project path whose configuration files are scanned.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the scan configuration files async selection with its documented bounds
    ///     and ordering.
    /// </returns>
    private async Task<Dictionary<string, string>> ScanConfigurationFilesAsync(string projectPath,
        CancellationToken cancellationToken)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory))
            return settings;

        var configFiles = Directory.GetFiles(directory, "appsettings*.json");
        foreach (var file in configFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            await TryScanConfigurationFileAsync(file, settings, cancellationToken);

        return settings;
    }

    /// <summary>
    ///     Parses one JSON configuration file and appends flattened values to the result
    ///     dictionary.
    /// </summary>
    /// <param name="file">Validated file path used by this operation.</param>
    /// <param name="settings">The analysis options whose bounds are validated.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task TryScanConfigurationFileAsync(string file, Dictionary<string, string> settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream =
                new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonConfigurationFlattener.Flatten(document.RootElement, string.Empty, settings);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[ProjectScannerService] Failed to parse JSON file: {File}", file);
        }
    }

    /// <summary>
    ///     Reads launchSettings.json Project profiles and extracts environment variables and the
    ///     first HTTP port.
    /// </summary>
    /// <param name="projectPath">The local project path whose configuration files are scanned.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the launch settings scan result projection derived from the verified
    ///     inputs.
    /// </returns>
    private async Task<LaunchSettingsScanResult> ScanLaunchSettingsAsync(string projectPath,
        CancellationToken cancellationToken)
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int? defaultPort = null;

        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory))
            return new LaunchSettingsScanResult(envVars, defaultPort);

        var launchSettingsPath = Path.Combine(directory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
            return new LaunchSettingsScanResult(envVars, defaultPort);

        try
        {
            await using var stream = new FileStream(launchSettingsPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("profiles", out var profiles))
                foreach (var profile in profiles.EnumerateObject())
                {
                    if (!IsProjectProfile(profile.Value))
                        continue;

                    ReadLaunchEnvironmentVariables(profile.Value, envVars);
                    defaultPort = ReadLaunchHttpPort(profile.Value);
                    break;
                }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "[ProjectScannerService] Operation canceled while parsing launchSettings.json");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ProjectScannerService] Error parsing launchSettings.json at {LaunchSettingsPath}",
                launchSettingsPath);
            if (ex is IOException or UnauthorizedAccessException) throw;
        }

        return new LaunchSettingsScanResult(envVars, defaultPort);
    }

    /// <summary>
    ///     Reads .env files from the parent directory first and then the project directory.
    /// </summary>
    /// <param name="projectPath">The local project path whose configuration files are scanned.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the scan dot env files async selection with its documented bounds and
    ///     ordering.
    /// </returns>
    private async Task<Dictionary<string, string>> ScanDotEnvFilesAsync(string projectPath,
        CancellationToken cancellationToken)
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(projectPath);

        if (string.IsNullOrEmpty(directory))
            return envVars;

        foreach (var dir in GetDotEnvSearchDirectories(directory))
            await TryScanDotEnvFileAsync(Path.Combine(dir, ".env"), envVars, cancellationToken);

        return envVars;
    }

    /// <summary>
    ///     Returns .env search directories in the same precedence order as the legacy scanner.
    /// </summary>
    /// <param name="projectDirectory">Directory containing the selected C# project.</param>
    /// <returns>The dot env search directories selection with its documented bounds and ordering.</returns>
    private static IEnumerable<string> GetDotEnvSearchDirectories(string projectDirectory)
    {
        var parentDir = Directory.GetParent(projectDirectory)?.FullName;
        if (!string.IsNullOrEmpty(parentDir))
            yield return parentDir;

        yield return projectDirectory;
    }

    /// <summary>
    ///     Parses one .env file when present and appends key-value pairs to the result dictionary.
    /// </summary>
    /// <param name="envFilePath">Local environment file to scan.</param>
    /// <param name="envVars">Runtime environment values; can contain secrets and must not be logged.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task TryScanDotEnvFileAsync(string envFilePath, Dictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(envFilePath))
            return;

        try
        {
            var lines = await File.ReadAllLinesAsync(envFilePath, cancellationToken);
            foreach (var line in lines)
                if (DotEnvLineParser.TryParse(line, out var variable))
                    envVars[variable.Key] = variable.Value;

            logger.LogInformation("[ProjectScannerService] Successfully scanned .env file: {EnvFilePath}",
                envFilePath);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex,
                "[ProjectScannerService] Operation canceled while scanning .env file: {EnvFilePath}", envFilePath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ProjectScannerService] Error scanning .env file: {EnvFilePath}", envFilePath);
            if (ex is IOException or UnauthorizedAccessException) throw;
        }
    }

    /// <summary>
    ///     Checks whether a launch profile represents the project executable profile.
    /// </summary>
    /// <param name="profile">
    ///     JSON value to validate or normalize without retaining arbitrary provider prose.
    /// </param>
    /// <returns>Whether the documented project profile condition holds.</returns>
    private static bool IsProjectProfile(JsonElement profile)
    {
        return profile.TryGetProperty("commandName", out var commandName) &&
               commandName.GetString() == "Project";
    }

    /// <summary>
    ///     Reads environment variables from a launch profile.
    /// </summary>
    /// <param name="profile">
    ///     JSON value to validate or normalize without retaining arbitrary provider prose.
    /// </param>
    /// <param name="envVars">Runtime environment values; can contain secrets and must not be logged.</param>
    private static void ReadLaunchEnvironmentVariables(JsonElement profile, Dictionary<string, string> envVars)
    {
        if (!profile.TryGetProperty("environmentVariables", out var envVarsElement))
            return;

        foreach (var envVar in envVarsElement.EnumerateObject())
            envVars[envVar.Name] = envVar.Value.GetString() ?? string.Empty;
    }

    /// <summary>
    ///     Reads the first HTTP application URL port from a launch profile.
    /// </summary>
    /// <param name="profile">
    ///     JSON value to validate or normalize without retaining arbitrary provider prose.
    /// </param>
    /// <returns>
    ///     The numeric position, count or status described by this operation, or null when no value
    ///     is available.
    /// </returns>
    private static int? ReadLaunchHttpPort(JsonElement profile)
    {
        if (!profile.TryGetProperty("applicationUrl", out var appUrlElement))
            return null;

        var urls = appUrlElement.GetString()?.Split(';');
        var httpUrl = urls?.FirstOrDefault(url => url.StartsWith("http://"));

        return httpUrl != null && Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri)
            ? uri.Port
            : null;
    }

    /// <summary>
    ///     Launch settings data extracted from a Project profile.
    /// </summary>
    /// <param name="EnvVars">Runtime environment values; can contain secrets and must not be logged.</param>
    /// <param name="DefaultPort">Recorded default application port, if available.</param>
    private readonly record struct LaunchSettingsScanResult(Dictionary<string, string> EnvVars, int? DefaultPort);
}