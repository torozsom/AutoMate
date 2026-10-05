using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>Rechecks operator policy and current deployment-owner consent without retaining diagnostic payloads.</summary>
public interface IAnalysisEgressAuthorizer
{
    /// <summary>Returns false for missing deployments, unapproved tenants, revoked consent or disabled triggers.</summary>
    Task<bool> AuthorizeAsync(Guid deploymentId, AiAnalysisTrigger trigger,
        CancellationToken cancellationToken = default);
}