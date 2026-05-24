using Core.DTO;

namespace Services.Templating;

/// <summary>
///     Service responsible for dynamically generating infrastructure-as-code files
///     (like Dockerfile, docker-compose.yml) based on project configurations.
/// </summary>
public interface ITemplatingService
{
    /// <summary>
    ///     Generates all necessary templates and saves them to the specified output directory.
    /// </summary>
    /// <param name="config">The deployment configuration.</param>
    /// <param name="metadata">The project metadata.</param>
    /// <param name="csProjectName">The name of the main C# project.</param>
    /// <param name="outputDirectory">The target directory for the generated files.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    Task GenerateAndSaveAllTemplatesAsync(DeploymentConfigDto config, ProjectMetadataDto metadata, string csProjectName,
        string outputDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Generates all matching templates and returns their content without saving them to disk.
    /// </summary>
    /// <param name="config">The deployment configuration.</param>
    /// <param name="metadata">The project metadata.</param>
    /// <param name="csProjectName">The name of the main C# project.</param>
    /// <param name="outputDirectory">The directory used as the relative-path anchor while rendering templates.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A list of generated template files with relative output paths and content.</returns>
    Task<List<TemplateFile>> GenerateAllTemplatesAsync(DeploymentConfigDto config, ProjectMetadataDto metadata,
        string csProjectName, string outputDirectory, CancellationToken cancellationToken = default);
}