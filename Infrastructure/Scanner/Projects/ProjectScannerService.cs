using Application.Abstractions.Scanning;
using Domain.Defaults;
using Domain.DTO.Deployment;
using Domain.DTO.Projects;
using Domain.Entities.Projects;
using Infrastructure.Scanner.Catalog;
using Infrastructure.Scanner.Environment;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Scanner.Projects;

/// <summary>
///     Coordinates C# project metadata scanning, dependency analysis, and environment-variable
///     extraction.
/// </summary>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class ProjectScannerService(ILogger<ProjectScannerService> logger) : IProjectScannerService
{
    /// <summary>Database provider rule source loaded from the scanner configuration file.</summary>
    private readonly DatabaseProviderRuleCatalog _databaseProviderRules = new(logger);

    /// <summary>Environment variable extractor for appsettings, launchSettings, and .env files.</summary>
    private readonly ProjectEnvironmentVariableExtractor _environmentVariableExtractor = new(logger);

    /// <summary>Allocates local host ports for generated deployment configurations.</summary>
    private readonly ScannerPortProvider _portProvider = new(logger);

    /// <summary>
    ///     Scans the provided project file path to extract solution-wide metadata.
    /// </summary>
    /// <param name="filePath">The full path to the .csproj file to scan.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task returning project metadata containing references and target framework.</returns>
    /// <exception cref="FileNotFoundException">The selected C# project file does not exist.</exception>
    /// <exception cref="T:System.OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    public async Task<ProjectMetadataDto> ScanProjectContentAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            logger.LogError("[ProjectScannerService] The project file '{FilePath}' does not exist.", filePath);
            throw new FileNotFoundException($"The project file '{filePath}' does not exist.");
        }

        return await ProjectDependencyGraphScanner.ScanAsync(filePath, cancellationToken);
    }

    /// <summary>
    ///     Analyzes the dependencies of the given project to determine deployment configurations.
    /// </summary>
    /// <param name="app">The parent project entity.</param>
    /// <param name="csProject">The specific C# project being analyzed.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task returning a deployment configuration including identified database dependencies.
    /// </returns>
    /// <exception cref="T:System.OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    public async Task<DeploymentConfigDto> AnalyzeDependenciesAsync(Domain.Entities.Projects.Application app,
        CsProject csProject,
        CancellationToken cancellationToken = default)
    {
        var config = CreateDefaultDeploymentConfig(app, csProject);

        try
        {
            var metadata = await ScanProjectContentAsync(csProject.Path, cancellationToken);
            var allPackages = metadata.PackageReferences.Keys
                .Concat(metadata.ReferencedProjectPackages.Keys)
                .ToList();

            var providerRules = await _databaseProviderRules.GetAsync(cancellationToken);
            foreach (var rule in providerRules)
            {
                if (!DoesRuleMatchPackages(rule, allPackages))
                    continue;

                config.Databases.Add(CreateDatabaseConfig(rule));

                logger.LogInformation(
                    "Database dependency detected for project {ProjectId}: {DbType}.",
                    app.Id, rule.DbType);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("[ProjectScannerService] Dependency analysis cancelled for project '{ProjectName}'." +
                              "Ex: {ExceptionMessage}", app.Name, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ProjectScannerService] Error scanning csproj dependencies for project: {ProjectName}",
                app.Name);
        }

        return config;
    }

    /// <summary>
    ///     Extracts environment variables from configuration files in the project directory.
    /// </summary>
    /// <param name="projectPath">The root path of the project containing configuration files.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task returning a dictionary of extracted environment variables.</returns>
    public async Task<Dictionary<string, string>> ExtractEnvironmentVariablesAsync(string projectPath,
        CancellationToken cancellationToken = default)
    {
        return await _environmentVariableExtractor.ExtractAsync(projectPath, cancellationToken);
    }

    /// <summary>
    ///     Creates the baseline local deployment configuration before dependency-specific defaults
    ///     are applied.
    /// </summary>
    /// <param name="app">The application whose project dependencies are being analyzed.</param>
    /// <param name="csProject">The selected C# project and its recorded metadata.</param>
    /// <returns>The deployment config dto projection derived from the verified inputs.</returns>
    private DeploymentConfigDto CreateDefaultDeploymentConfig(Domain.Entities.Projects.Application app,
        CsProject csProject)
    {
        return new DeploymentConfigDto
        {
            ProjectId = app.Id,
            CsProjectId = csProject.Id,
            ProjectName = app.Name,
            ExposedPort = _portProvider.GetAvailablePort(),
            EnvironmentName = DeploymentDefaults.DevelopmentEnvironmentName,
            Databases = []
        };
    }

    /// <summary>
    ///     Checks whether any discovered package matches a database provider rule.
    /// </summary>
    /// <param name="rule">Provider detection or template selection rule.</param>
    /// <param name="packageNames">Dependency package names checked against provider rules.</param>
    /// <returns>Whether the documented rule match packages condition holds.</returns>
    private static bool DoesRuleMatchPackages(DbProviderRuleDto rule, IEnumerable<string> packageNames)
    {
        return packageNames.Any(projectPackage =>
            rule.Packages.Any(rulePackage =>
                projectPackage.Contains(rulePackage, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    ///     Creates the deployment database configuration implied by a matched provider rule.
    /// </summary>
    /// <param name="rule">Provider detection or template selection rule.</param>
    /// <returns>The database config dto projection derived from the verified inputs.</returns>
    private static DatabaseConfigDto CreateDatabaseConfig(DbProviderRuleDto rule)
    {
        return new DatabaseConfigDto
        {
            DbType = rule.DbType,
            ContainerNameSuffix = rule.DbType.ToLower(),
            ConnectionStringName = $"{rule.DbType}Connection"
        };
    }
}
