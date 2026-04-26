using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Core.DTO;
using Core.Entities;

namespace Services.Scanner;

/// <summary>
///     Provides functionality to scan and analyze C# project files (.csproj) to extract
///     metadata such as target frameworks, dependencies, and project references.
/// </summary>
public class ProjectScannerService : IProjectScannerService
{
    private static readonly string DbProvidersJsonPath
        = Path.Combine(AppContext.BaseDirectory, "Scanner", "database-providers.json");

    /// <summary>
    ///     Scans a solution's project files (.csproj) to extract metadata such as target frameworks,
    ///     dependencies, and project references.
    /// </summary>
    /// <param name="filePath">The path to the project (.csproj) file to scan.</param>
    /// <returns>
    ///     A <see cref="ProjectMetadataDto" /> object containing metadata such as the target
    ///     framework, project references, package references, and other details.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    ///     Thrown when the specified project file does not exist at the given path.
    /// </exception>
    public async Task<ProjectMetadataDto> ScanProjectContentAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"The project file '{filePath}' does not exist.");

        // Scan the main project file first
        var mainContent = await File.ReadAllTextAsync(filePath);
        var mainMetadata = await ScanCsprojFileContentAsync(mainContent);

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
            var (currentPath, currentMetadata) = projectsToProcess.Dequeue();
            var currentDir = Path.GetDirectoryName(currentPath) ?? string.Empty;

            foreach (var relativeRef in currentMetadata.ProjectReferences)
            {
                var absoluteRefPath = Path.GetFullPath(Path.Combine(currentDir, relativeRef));
                if (visitedPaths.Contains(absoluteRefPath) || !File.Exists(absoluteRefPath)) continue;

                visitedPaths.Add(absoluteRefPath);
                allProjectPaths.Add(absoluteRefPath);

                var refContent = await File.ReadAllTextAsync(absoluteRefPath);
                var refMetadata = await ScanCsprojFileContentAsync(refContent);

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


    /// <summary>
    ///     Analyzes the dependencies of a given project and its associated C# project file (.csproj) to determine
    ///     if the project requires a database and identifies the type of database based on the presence of specific
    ///     package references.
    /// </summary>
    /// <param name="project">The project to be analyzed.</param>
    /// <param name="csProject">The web based C# project.</param>
    /// <returns></returns>
    public async Task<DeploymentConfigDto> AnalyzeDependenciesAsync(Project project, CsProject csProject)
    {
        var config = new DeploymentConfigDto
        {
            ProjectId = project.Id,
            CsProjectId = csProject.Id,
            ProjectName = project.Name,
            ExposedPort = GetAvailablePort(),
            RequiresDb = false,
            DbType = "PostgreSQL",
            EnvironmentName = "Development"
        };

        try
        {
            // Scan the main project and all its referenced projects to gather metadata about package references
            var metadata = await ScanProjectContentAsync(csProject.Path);

            // Combine all package references from the main project and its referenced projects
            var allPackages = metadata.PackageReferences.Keys
                .Concat(metadata.ReferencedProjectPackages.Keys)
                .ToList();

            if (File.Exists(DbProvidersJsonPath))
            {
                // Read the database providers configuration from the JSON file
                var jsonContent = await File.ReadAllTextAsync(DbProvidersJsonPath);
                var providerRules = JsonSerializer.Deserialize<List<DbProviderRuleDto>>(jsonContent);

                // Determine if any of the packages match known database providers
                if (providerRules != null)
                    foreach (var rule in providerRules)
                    {
                        var isMatch = allPackages.Any(projPkg =>
                            rule.Packages.Any(rulePkg =>
                                projPkg.Contains(rulePkg, StringComparison.OrdinalIgnoreCase)));

                        if (!isMatch) continue;
                        config.RequiresDb = true;
                        config.DbType = rule.DbType;
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error scanning csproj dependencies: {ex.Message}");
        }

        return config;
    }


    /// <summary>
    ///     Extracts environment variables from configuration files in the project directory. This method scans for
    ///     appsettings.json files, launchSettings.json, and .env files to gather environment variable definitions.
    /// </summary>
    /// <param name="projectPath">The project path where the configuration files are found.</param>
    /// <returns></returns>
    public async Task<Dictionary<string, string>> ExtractEnvironmentVariablesAsync(string projectPath)
    {
        var extractedVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Scan the project directory for configuration files and extract their contents
            var appSettings = await ScanConfigurationFilesAsync(projectPath);
            var launchData = await ScanLaunchSettingsAsync(projectPath);
            var dotEnvData = await ScanDotEnvFilesAsync(projectPath);

            // Combine all extracted environment variables
            foreach (var kvp in appSettings) extractedVars[kvp.Key] = kvp.Value;
            foreach (var kvp in dotEnvData) extractedVars[kvp.Key] = kvp.Value;
            foreach (var kvp in launchData.EnvVars) extractedVars[kvp.Key] = kvp.Value;

            // Environment type settings are handled by the dropdown.
            extractedVars.Remove("ASPNETCORE_ENVIRONMENT");
            extractedVars.Remove("DOTNET_ENVIRONMENT");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARNING] Error extracting environment variables from config files: {ex.Message}");
        }

        return extractedVars;
    }


    /// <summary>
    ///     Parses the provided project XML content and extracts metadata such as target framework,
    ///     dependencies, project references, and other configuration details.
    /// </summary>
    /// <param name="xmlContent">The XML content of the project file to scan.</param>
    /// <returns>
    ///     A <see cref="ProjectMetadataDto" /> object containing information about the project's
    ///     target framework, .NET version, package references, project references, and other metadata.
    /// </returns>
    /// <exception cref="XmlException">
    ///     Thrown when the provided XML content is not well-formed or cannot be parsed.
    /// </exception>
    private static Task<ProjectMetadataDto> ScanCsprojFileContentAsync(string xmlContent)
    {
        // Parse the XML content of the project file
        var document = XDocument.Parse(xmlContent);
        var targetFramework = document.Descendants("TargetFramework").FirstOrDefault()?.Value;

        // If TargetFramework is not specified, default to .NET 10.0
        if (string.IsNullOrEmpty(targetFramework))
            targetFramework = "net10.0";

        var dotNetVersion = targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? targetFramework[3..]
            : "10.0";

        // Check if the project is a web project by looking for the Sdk attribute in the root element
        var sdkAttribute = document.Root?.Attribute("Sdk")?.Value;
        var isWebProject = sdkAttribute != null
                           && sdkAttribute.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);

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

            if (string.IsNullOrEmpty(include)) continue;
            var normalizedPath = include.Replace('\\', '/');
            projectReferences.Add(normalizedPath);
        }

        // Create and return the metadata DTO with the extracted information
        var metadata = new ProjectMetadataDto
        {
            TargetFramework = targetFramework,
            DotNetVersion = dotNetVersion,
            IsWebProject = isWebProject,
            UserSecretsId = userSecretsId,
            PackageReferences = packageReferences,
            ProjectReferences = projectReferences
        };

        return Task.FromResult(metadata);
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
    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }


    /// <summary>
    ///     Scans the directory of the given project path for configuration files (e.g., appsettings.json)
    ///     and extracts their contents into a flattened dictionary format. This method looks for files matching
    ///     the pattern "appsettings*.json", reads their content, and flattens the JSON structure into a key-value
    ///     pairs where nested properties are represented with double underscores.
    /// </summary>
    /// <param name="projectPath">The path of the project to be scanned.</param>
    /// <returns></returns>
    private static async Task<Dictionary<string, string>> ScanConfigurationFilesAsync(string projectPath)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory)) return settings;

        var configFiles = Directory.GetFiles(directory, "appsettings*.json");

        foreach (var file in configFiles.OrderBy(f => f.Length))
        {
            var content = await File.ReadAllTextAsync(file);
            using var doc = JsonDocument.Parse(content);
            FlattenJsonElement(doc.RootElement, "", settings);
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
    /// <returns>
    ///     A tuple containing a dictionary of environment variables and an optional
    ///     integer representing the default port.
    /// </returns>
    private static async Task<(Dictionary<string, string> EnvVars, int? DefaultPort)> ScanLaunchSettingsAsync(
        string projectPath)
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
            // Read and parse the launchSettings.json file to extract environment variables and application URLs
            var content = await File.ReadAllTextAsync(launchSettingsPath);
            using var doc = JsonDocument.Parse(content);

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
        catch (Exception ex)
        {
            Console.WriteLine($"[WARNING] Error parsing launchSettings.json: {ex.Message}");
        }

        return (envVars, defaultPort);
    }


    /// <summary>
    ///     Scans for .env files in the project directory and its parent directory to extract environment variables.
    /// </summary>
    /// <param name="projectPath"></param>
    /// <returns></returns>
    private static async Task<Dictionary<string, string>> ScanDotEnvFilesAsync(string projectPath)
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
                var lines = await File.ReadAllLinesAsync(envFilePath);
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    // Ignore empty lines and comments
                    if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                        continue;

                    // Extract key-value pairs in the format KEY=VALUE
                    var splitIndex = trimmedLine.IndexOf('=');
                    if (splitIndex > 0)
                    {
                        var key = trimmedLine[..splitIndex].Trim();
                        var value = trimmedLine[(splitIndex + 1)..].Trim();

                        // Remove surrounding quotes if present (handles both " and ')
                        if (value.Length > 1 &&
                            ((value.StartsWith('"') && value.EndsWith('"')) ||
                             (value.StartsWith('\'') && value.EndsWith('\''))))
                            value = value[1..^1];
                        envVars[key] = value;
                    }
                }

                Console.WriteLine($"[INFO] Successfully scanned .env file: {envFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Error scanning .env file: ({envFilePath}): {ex.Message}");
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