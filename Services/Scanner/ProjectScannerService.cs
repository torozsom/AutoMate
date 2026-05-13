using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Core.DTO;
using Core.Entities;
using Microsoft.Extensions.Logging;

namespace Services.Scanner;

/// <summary>
///     Provides functionality to scan and analyze C# project files (.csproj) to extract
///     metadata such as target frameworks, dependencies, and project references.
/// </summary>
public class ProjectScannerService(ILogger<ProjectScannerService> logger) : IProjectScannerService
{
    private static readonly string DbProvidersJsonPath
        = Path.Combine(AppContext.BaseDirectory, "Scanner", "database-providers.json");

    private static readonly SemaphoreSlim DbProvidersSemaphore = new(1, 1);
    private static List<DbProviderRuleDto>? _cachedDbProviders;


    /// <inheritdoc />
    public async Task<ProjectMetadataDto> ScanProjectContentAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            logger.LogError("[ProjectScannerService] The project file '{FilePath}' does not exist.", filePath);
            throw new FileNotFoundException($"The project file '{filePath}' does not exist.");
        }

        // Start by scanning the main project file to extract its metadata
        var mainMetadata = await ScanCsprojFileContentAsync(filePath, cancellationToken);

        // Initialize data structures to track referenced projects and their packages
        var referencedProjectPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectsToProcess = new Queue<(string Path, ProjectMetadataDto Metadata)>();

        var fullFilePath = Path.GetFullPath(filePath);
        allProjectPaths.Add(fullFilePath);
        visitedPaths.Add(fullFilePath);

        // Start with the main project
        projectsToProcess.Enqueue((fullFilePath, mainMetadata));

        // Process the queue of projects to scan their references recursively
        while (projectsToProcess.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (currentPath, currentMetadata) = projectsToProcess.Dequeue();
            var currentDir = Path.GetDirectoryName(currentPath) ?? string.Empty;

            foreach (var relativeRef in currentMetadata.ProjectReferences)
            {
                var absoluteRefPath = Path.GetFullPath(Path.Combine(currentDir, relativeRef));
                if (visitedPaths.Contains(absoluteRefPath) || !File.Exists(absoluteRefPath)) continue;

                visitedPaths.Add(absoluteRefPath);
                allProjectPaths.Add(absoluteRefPath);

                var refMetadata = await ScanCsprojFileContentAsync(absoluteRefPath, cancellationToken);

                foreach (var pkg in refMetadata.PackageReferences)
                    referencedProjectPackages.TryAdd(pkg.Key, pkg.Value);

                projectsToProcess.Enqueue((absoluteRefPath, refMetadata));
            }
        }

        // Combine the package references from all projects
        return mainMetadata with
        {
            ReferencedProjectPackages = referencedProjectPackages,
            AllProjectPaths = allProjectPaths
        };
    }


    /// <inheritdoc />
    public async Task<DeploymentConfigDto> AnalyzeDependenciesAsync(Project project, CsProject csProject,
        CancellationToken cancellationToken = default)
    {
        var config = new DeploymentConfigDto
        {
            ProjectId = project.Id,
            CsProjectId = csProject.Id,
            ProjectName = project.Name,
            ExposedPort = GetAvailablePort(),
            EnvironmentName = "Development",
            Databases = []
        };

        try
        {
            var metadata = await ScanProjectContentAsync(csProject.Path, cancellationToken);

            // Combine all package references from the main project and its referenced projects
            var allPackages = metadata.PackageReferences.Keys
                .Concat(metadata.ReferencedProjectPackages.Keys)
                .ToList();

            var providerRules = await GetDbProviderRulesAsync(cancellationToken);

            // Determine if any of the packages match known database providers
            if (providerRules != null)
                foreach (var rule in providerRules)
                {
                    var isMatch = allPackages.Any(projPkg =>
                        rule.Packages.Any(rulePkg =>
                            projPkg.Contains(rulePkg, StringComparison.OrdinalIgnoreCase)));

                    if (isMatch)
                    {
                        config.Databases.Add(new DatabaseConfigDto
                        {
                            DbType = rule.DbType,
                            ContainerNameSuffix = rule.DbType.ToLower(),
                            ConnectionStringName = $"{rule.DbType}Connection"
                        });

                        logger.LogInformation(
                            "[ProjectScannerService] Database dependency detected for project '{ProjectName}': {DbType}",
                            project.Name, rule.DbType);
                    }
                }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("[ProjectScannerService] Dependency analysis cancelled for project '{ProjectName}'." +
                              "Ex: {ExceptionMessage}", project.Name, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ProjectScannerService] Error scanning csproj dependencies for project: {ProjectName}",
                project.Name);
        }

        return config;
    }


    /// <inheritdoc />
    public async Task<Dictionary<string, string>> ExtractEnvironmentVariablesAsync(string projectPath,
        CancellationToken cancellationToken = default)
    {
        var extractedVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var appSettings = await ScanConfigurationFilesAsync(projectPath, cancellationToken);
            var launchData = await ScanLaunchSettingsAsync(projectPath, cancellationToken);
            var dotEnvData = await ScanDotEnvFilesAsync(projectPath, cancellationToken);

            // Combine all extracted environment variables
            foreach (var kvp in appSettings) extractedVars[kvp.Key] = kvp.Value;
            foreach (var kvp in dotEnvData) extractedVars[kvp.Key] = kvp.Value;
            foreach (var kvp in launchData.EnvVars) extractedVars[kvp.Key] = kvp.Value;

            // Environment type settings are handled by the dropdown.
            extractedVars.Remove("ASPNETCORE_ENVIRONMENT");
            extractedVars.Remove("DOTNET_ENVIRONMENT");
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
        }

        return extractedVars;
    }


    /// <summary>
    ///     Retrieves the list of database provider rules from a JSON file. This method implements caching to avoid
    ///     repeated file reads and deserialization, ensuring that the database provider rules are loaded efficiently
    ///     and are available for use when analyzing project dependencies.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token used to cancel the asynchronous operation.</param>
    /// <returns>
    ///     A list of <see cref="DbProviderRuleDto" /> objects representing the database provider rules defined
    ///     in the JSON file, or null if the file is not found or cannot be parsed.
    /// </returns>
    private async Task<List<DbProviderRuleDto>?> GetDbProviderRulesAsync(CancellationToken cancellationToken)
    {
        if (_cachedDbProviders != null)
            return _cachedDbProviders;

        if (!File.Exists(DbProvidersJsonPath))
        {
            logger.LogWarning(
                "[ProjectScannerService] Database providers JSON file not found at: {DbProvidersJsonPath}",
                DbProvidersJsonPath);
            return null;
        }

        await DbProvidersSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedDbProviders != null) return _cachedDbProviders;

            // Try to read the database providers JSON file
            await using var stream = new FileStream(DbProvidersJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, true);
            _cachedDbProviders =
                await JsonSerializer.DeserializeAsync<List<DbProviderRuleDto>>(stream,
                    cancellationToken: cancellationToken);

            return _cachedDbProviders;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "[ProjectScannerService] Operation canceled while parsing database-providers.json");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ProjectScannerService] Failed to parse database-providers.json");
            return null;
        }
        finally
        {
            DbProvidersSemaphore.Release();
        }
    }


    /// <summary>
    ///     Parses the provided project XML content and extracts metadata such as target framework,
    ///     dependencies, project references, and other configuration details.
    /// </summary>
    /// <param name="filePath">The path to the project file to scan.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A <see cref="ProjectMetadataDto" /> object containing information about the project's
    ///     target framework, .NET version, package references, project references, and other metadata.
    /// </returns>
    /// <exception cref="XmlException">
    ///     Thrown when the provided XML content is not well-formed or cannot be parsed.
    /// </exception>
    private static async Task<ProjectMetadataDto> ScanCsprojFileContentAsync(string filePath,
        CancellationToken cancellationToken)
    {
        // Try to read the project file content
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        var targetFramework = document.Descendants("TargetFramework").FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(targetFramework)) targetFramework = "net10.0";

        var dotNetVersion = targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? targetFramework[3..]
            : "10.0";

        // Check if the project is a web project by looking for the Sdk attribute in the root element
        var sdkAttribute = document.Root?.Attribute("Sdk")?.Value;
        var isWebProject = sdkAttribute != null &&
                           sdkAttribute.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);

        // Extract the UserSecretsId if it exists
        var userSecretsId = document.Descendants("UserSecretsId").FirstOrDefault()?.Value;

        // Extract package references and project references
        var packageReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in document.Descendants("PackageReference"))
        {
            var include = pr.Attribute("Include")?.Value;
            var version = pr.Attribute("Version")?.Value;
            if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
                packageReferences[include] = version;
        }

        // Extract project references
        var projectReferences = new List<string>();
        foreach (var pr in document.Descendants("ProjectReference"))
        {
            var include = pr.Attribute("Include")?.Value;
            if (!string.IsNullOrEmpty(include)) projectReferences.Add(include.Replace('\\', '/'));
        }

        return new ProjectMetadataDto
        {
            TargetFramework = targetFramework,
            DotNetVersion = dotNetVersion,
            IsWebProject = isWebProject,
            UserSecretsId = userSecretsId,
            PackageReferences = packageReferences,
            ProjectReferences = projectReferences
        };
    }


    /// <summary>
    ///     Determines and returns an available port on the local machine. This method creates a temporary
    ///     listener to identify a free port and then releases it for future use.
    /// </summary>
    /// <returns>
    ///     An integer representing a port number that is currently available for use.
    /// </returns>
    /// <exception cref="SocketException">
    ///     Thrown when an error occurs while accessing the network during the process of finding an available port.
    /// </exception>
    private int GetAvailablePort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            logger.LogInformation("[ProjectScannerService] Successfully allocated dynamic port: {Port}", port);
            return port;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[ProjectScannerService] Failed to find an available port. Falling back to default port 8080.");
            return 8080;
        }
    }


    /// <summary>
    ///     Scans the directory of the given project path for configuration files (e.g., appsettings.json)
    ///     and extracts their contents into a flattened dictionary format. This method looks for files matching
    ///     the pattern "appsettings*.json", reads their content, and flattens the JSON structure into a key-value
    ///     pairs where nested properties are represented with double underscores.
    /// </summary>
    /// <param name="projectPath">The path of the project to be scanned.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns></returns>
    private async Task<Dictionary<string, string>> ScanConfigurationFilesAsync(string projectPath,
        CancellationToken cancellationToken)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory)) return settings;

        var configFiles = Directory.GetFiles(directory, "appsettings*.json");

        foreach (var file in configFiles.OrderBy(f => f.Length))
            try
            {
                // Use a FileStream with asynchronous reading to efficiently read the JSON file content
                await using var stream =
                    new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                FlattenJsonElement(doc.RootElement, "", settings);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "[ProjectScannerService] Failed to parse JSON file: {File}", file);
            }

        return settings;
    }


    /// <summary>
    ///     Scans the launchSettings.json file associated with the project to extract environment variables
    ///     and determine the default port for the application. This method looks for the launchSettings.json
    ///     file in the Properties folder of the project directory, parses its content, and retrieves any
    ///     environment variables.
    /// </summary>
    /// <param name="projectPath">The path of the project to be scanned.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A tuple containing a dictionary of environment variables and an optional
    ///     integer representing the default port.
    /// </returns>
    private async Task<(Dictionary<string, string> EnvVars, int? DefaultPort)> ScanLaunchSettingsAsync(
        string projectPath, CancellationToken cancellationToken)
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int? defaultPort = null;

        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory))
            return (envVars, defaultPort);

        // Look for launchSettings.json in the Properties folder of the project directory
        var launchSettingsPath = Path.Combine(directory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
            return (envVars, defaultPort);

        try
        {
            await using var stream = new FileStream(launchSettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, true);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("profiles", out var profiles))
                foreach (var profile in profiles.EnumerateObject())
                {
                    // Look for the profile that has "commandName" set to "Project"
                    if (!profile.Value.TryGetProperty("commandName", out var commandName) ||
                        commandName.GetString() != "Project") continue;

                    // Extract environment variables defined in the profile
                    if (profile.Value.TryGetProperty("environmentVariables", out var envVarsElement))
                        foreach (var envVar in envVarsElement.EnumerateObject())
                            envVars[envVar.Name] = envVar.Value.GetString() ?? "";

                    // Extract the application URL to determine the default port if it's defined
                    if (profile.Value.TryGetProperty("applicationUrl", out var appUrlElement))
                    {
                        var urls = appUrlElement.GetString()?.Split(';');
                        var httpUrl = urls?.FirstOrDefault(u => u.StartsWith("http://"));

                        if (httpUrl != null && Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri))
                            defaultPort = uri.Port;
                    }

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
        }

        return (envVars, defaultPort);
    }


    /// <summary>
    ///     Scans for .env files in the project directory and its parent directory to extract environment variables.
    /// </summary>
    /// <param name="projectPath">The path to the project directory to scan for .env files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary of environment variables.</returns>
    private async Task<Dictionary<string, string>> ScanDotEnvFilesAsync(string projectPath,
        CancellationToken cancellationToken)
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(projectPath);

        if (string.IsNullOrEmpty(directory)) return envVars;

        // Scan the project directory and its parent directory for .env files, prioritizing the project directory
        var directoriesToScan = new List<string> { directory };
        var parentDir = Directory.GetParent(directory)?.FullName;

        if (!string.IsNullOrEmpty(parentDir))
            directoriesToScan.Insert(0, parentDir);

        // Look for .env files in the specified directories and extract environment variables
        foreach (var dir in directoriesToScan)
        {
            var envFilePath = Path.Combine(dir, ".env");
            if (!File.Exists(envFilePath)) continue;

            try
            {
                // Read the .env file line by line to extract key-value pairs, ignoring comments and empty lines
                var lines = await File.ReadAllLinesAsync(envFilePath, cancellationToken);
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    // Ignore empty lines and comments
                    if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                        continue;

                    // Remove inline comments by splitting on the first occurrence of " #"
                    var commentIndex = trimmedLine.IndexOf(" #", StringComparison.Ordinal);
                    if (commentIndex > 0)
                        trimmedLine = trimmedLine[..commentIndex].Trim();

                    // Split the line into key and value based on the first occurrence of '='
                    var splitIndex = trimmedLine.IndexOf('=');
                    if (splitIndex > 0)
                    {
                        var key = trimmedLine[..splitIndex].Trim();
                        var value = trimmedLine[(splitIndex + 1)..].Trim();

                        if (value.Length > 1 &&
                            ((value.StartsWith('"') && value.EndsWith('"')) ||
                             (value.StartsWith('\'') && value.EndsWith('\''))))
                            value = value[1..^1];

                        envVars[key] = value;
                    }
                }

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
            }
        }

        return envVars;
    }


    /// <summary>
    ///     Recursively flattens a JSON element into a dictionary of key-value pairs. Nested properties are
    ///     represented with keys that concatenate parent and child property names using double underscores.
    ///     This method handles JSON objects and arrays, ensuring that all values are extracted and stored in
    ///     a flattened format suitable for configuration settings.
    /// </summary>
    /// <param name="element">The JSON element to be formatted.</param>
    /// <param name="prefix">
    ///     The prefix to be used for nested keys, representing the hierarchy of the JSON structure.
    /// </param>
    /// <param name="result">The dictionary where the flattened key-value pairs will be stored.</param>
    private static void FlattenJsonElement(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var newKey = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}__{property.Name}";
                    FlattenJsonElement(property.Value, newKey, result);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var newKey = $"{prefix}__{index}";
                    FlattenJsonElement(item, newKey, result);
                    index++;
                }

                break;

            default:
                result[prefix] = element.ToString();
                break;
        }
    }
}