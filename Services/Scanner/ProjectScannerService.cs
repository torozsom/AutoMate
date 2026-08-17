using Core.Defaults;
using Core.DTO;
using Core.Entities;
using Microsoft.Extensions.Logging;

namespace Services.Scanner;

/// <summary>
///     Coordinates C# project metadata scanning, dependency analysis, and environment-variable extraction.
/// </summary>
public sealed class ProjectScannerService(ILogger<ProjectScannerService> logger) : IProjectScannerService
{
    /// <summary>
    ///     Database provider rule source loaded from the scanner configuration file.
    /// </summary>
    private readonly DatabaseProviderRuleCatalog _databaseProviderRules = new(logger);

    /// <summary>
    ///     Environment variable extractor for appsettings, launchSettings, and .env files.
    /// </summary>
    private readonly ProjectEnvironmentVariableExtractor _environmentVariableExtractor = new(logger);

    /// <summary>
    ///     Allocates local host ports for generated deployment configurations.
    /// </summary>
    private readonly ScannerPortProvider _portProvider = new(logger);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<DeploymentConfigDto> AnalyzeDependenciesAsync(Application app, CsProject csProject,
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
                    "[ProjectScannerService] Database dependency detected for project '{ProjectName}': {DbType}",
                    app.Name, rule.DbType);
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

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> ExtractEnvironmentVariablesAsync(string projectPath,
        CancellationToken cancellationToken = default)
    {
        return await _environmentVariableExtractor.ExtractAsync(projectPath, cancellationToken);
    }

    /// <summary>
    ///     Creates the baseline local deployment configuration before dependency-specific defaults are applied.
    /// </summary>
    private DeploymentConfigDto CreateDefaultDeploymentConfig(Application app, CsProject csProject)
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
    private static bool DoesRuleMatchPackages(DbProviderRuleDto rule, IEnumerable<string> packageNames)
    {
        return packageNames.Any(projectPackage =>
            rule.Packages.Any(rulePackage =>
                projectPackage.Contains(rulePackage, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    ///     Creates the deployment database configuration implied by a matched provider rule.
    /// </summary>
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