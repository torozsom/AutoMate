using Domain.Entities;

namespace Application.Data.Apps;

/// <summary>Owner-scoped metadata read for one historical deployment.</summary>
public interface IDeploymentDetailsService
{
    /// <summary>Reads a selected deployment and its project identity; never resolves the latest deployment instead.</summary>
    Task<Deployment?> GetAsync(Guid owner, Guid project, Guid deployment, CancellationToken token = default);
}