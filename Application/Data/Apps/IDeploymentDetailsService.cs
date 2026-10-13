using Domain.Entities;

namespace Application.Data.Apps;

/// <summary>
///     Owner-scoped metadata read for one historical deployment.
/// </summary>
public interface IDeploymentDetailsService
{
    /// <summary>
    ///     Reads a selected deployment and its project identity; never resolves the latest
    ///     deployment instead.
    /// </summary>
    /// <param name="owner">The internal owner account identity used for authorization.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the exact owned historical deployment, or null when missing or
    ///     unauthorized.
    /// </returns>
    Task<Deployment?> GetAsync(Guid owner, Guid project, Guid deployment, CancellationToken token = default);
}
