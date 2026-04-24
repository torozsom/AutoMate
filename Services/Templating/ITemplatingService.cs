using Core.DTO;

namespace Services.Templating;

/// <summary>
///     Service responsible for dynamically generating infrastructure-as-code files
///     (like Dockerfile, docker-compose.yml) based on project configurations.
/// </summary>
public interface ITemplateService
{
    /// Generates all necessary templates (Dockerfile, .dockerignore, docker-compose.yml) and saves them to the specified output directory.
    Task GenerateAndSaveAllTemplatesAsync(DeploymentConfigDto config, ProjectMetadataDto metadata, string csProjectName, string outputDirectory);
}