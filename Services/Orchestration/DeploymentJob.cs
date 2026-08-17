using Core.DTO;

namespace Services.Orchestration;

/// <summary>
///     Represents a queued deployment operation processed outside the Blazor component lifecycle.
/// </summary>
public abstract record DeploymentJob
{
    /// <summary>
    ///     Gets the AutoMate application/project ID affected by the job.
    /// </summary>
    public abstract Guid ProjectId { get; }
}

/// <summary>
///     Queued request to deploy a local Docker Compose project.
/// </summary>
/// <param name="Config">The deployment configuration selected by the user.</param>
public sealed record LocalDeploymentJob(DeploymentConfigDto Config) : DeploymentJob
{
    /// <inheritdoc />
    public override Guid ProjectId => Config.ProjectId;
}

/// <summary>
///     Queued request to prepare and start a GitHub/Azure cloud deployment.
/// </summary>
/// <param name="Request">The cloud deployment request selected by the user.</param>
public sealed record CloudDeploymentJob(CloudDeploymentRequestDto Request) : DeploymentJob
{
    /// <inheritdoc />
    public override Guid ProjectId => Request.Config.ProjectId;
}

/// <summary>
///     Queued request to stop a local Docker Compose deployment.
/// </summary>
public sealed record StopLocalDeploymentJob : DeploymentJob
{
    /// <summary>
    ///     Creates a stop request for a local deployment.
    /// </summary>
    /// <param name="projectId">The AutoMate project ID whose deployment should stop.</param>
    /// <param name="projectName">The project name used to resolve the Docker Compose project name.</param>
    /// <param name="csProjectPath">The selected C# project path used to locate generated deployment files.</param>
    public StopLocalDeploymentJob(Guid projectId, string projectName, string csProjectPath)
    {
        ProjectId = projectId;
        ProjectName = projectName;
        CsProjectPath = csProjectPath;
    }

    /// <inheritdoc />
    public override Guid ProjectId { get; }

    /// <summary>
    ///     The project name used to resolve the Docker Compose project name.
    /// </summary>
    public string ProjectName { get; }

    /// <summary>
    ///     The selected C# project path used to locate generated deployment files.
    /// </summary>
    public string CsProjectPath { get; }
}