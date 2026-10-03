using Domain.DTO;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>Protected, token-free inputs needed to resume a SaaS launch on another worker.</summary>
internal sealed record CloudRunSnapshot(
    DeploymentConfigDto Config,
    ProjectMetadataDto Metadata,
    string CsProjectName,
    string RepositoryRoot);
