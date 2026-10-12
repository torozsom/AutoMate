using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>
///     Rechecks operator policy and current deployment-owner consent without retaining
///     diagnostic payloads.
/// </summary>
public interface IAnalysisEgressAuthorizer
{
    /// <summary>
    ///     Returns false for missing deployments, unapproved tenants, revoked consent or disabled
    ///     triggers.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="trigger">
    ///     The manual or automatic trigger; it does not grant provider egress permission.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning true only when current ownership, consent and operator policy permit
    ///     egress.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    Task<bool> AuthorizeAsync(Guid deploymentId, AiAnalysisTrigger trigger,
        CancellationToken cancellationToken = default);
}