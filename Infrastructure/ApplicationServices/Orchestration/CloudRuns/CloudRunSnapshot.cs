using Domain.DTO;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>
///     Protected, token-free inputs needed to resume a SaaS launch on another worker.
/// </summary>
/// <param name="Config">The deployment configuration used by the operation.</param>
/// <param name="Metadata">
///     The non-secret project metadata supplied to evidence or artifact generation.
/// </param>
/// <param name="CsProjectName">The selected C# project name used for generated artifacts.</param>
/// <param name="RepositoryRoot">The selected repository root used for deployment artifact paths.</param>
internal sealed record CloudRunSnapshot(
    DeploymentConfigDto Config,
    ProjectMetadataDto Metadata,
    string CsProjectName,
    string RepositoryRoot);