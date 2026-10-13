using Application.Abstractions.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>
///     Bounds process-local live collection leases; stores no log content or credentials.
/// </summary>
/// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
public sealed class DeploymentRuntimeViewers(TimeProvider clock) : IDeploymentRuntimeViewers
{
    /// <summary>Upper bound on tracked authorized viewing leases.</summary>
    private const int MaximumLeases = 4096;

    /// <summary>Serializes bounded viewing-lease updates.</summary>
    private readonly object _gate = new();

    /// <summary>Expiring authorized connection/project viewing leases.</summary>
    private readonly Dictionary<(string Connection, Guid Project), (Guid Deployment, DateTimeOffset Until)>
        _leases = [];

    /// <summary>
    ///     Renews an authorized connection's interest in one deployment.
    /// </summary>
    /// <param name="connectionId">The authorized connection whose expiring viewing lease is tracked.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <exception cref="InvalidOperationException">
    ///     Live runtime viewing capacity reached; try again shortly.
    /// </exception>
    public void Renew(string connectionId, Guid projectId, Guid deploymentId)
    {
        lock (_gate)
        {
            Prune();
            var key = (connectionId, projectId);
            if (!_leases.ContainsKey(key) && _leases.Count >= MaximumLeases)
                throw new InvalidOperationException("Live runtime viewing capacity reached; try again shortly.");
            _leases[key] = (deploymentId, clock.GetUtcNow().AddSeconds(45));
        }
    }

    /// <summary>
    ///     Removes a connection, optionally only for one project.
    /// </summary>
    /// <param name="connectionId">The authorized connection whose expiring viewing lease is tracked.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    public void Remove(string connectionId, Guid? projectId = null)
    {
        lock (_gate)
        {
            foreach (var key in _leases.Keys.Where(k => k.Connection == connectionId &&
                                                        (!projectId.HasValue || k.Project == projectId)).ToArray())
                _leases.Remove(key);
        }
    }

    /// <summary>
    ///     Checks whether a deployment has an unexpired viewer.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <returns>Whether the deployment has at least one unexpired authorized viewing lease.</returns>
    public bool HasViewers(Guid projectId, Guid deploymentId)
    {
        lock (_gate)
        {
            Prune();
            return _leases.Any(p => p.Key.Project == projectId && p.Value.Deployment == deploymentId);
        }
    }

    /// <summary>
    ///     Expires abandoned connections, including clients that never disconnect cleanly.
    /// </summary>
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _leases.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
            _leases.Remove(key);
    }
}
