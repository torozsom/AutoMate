using Application.Abstractions.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>Bounds process-local live collection leases; stores no log content or credentials.</summary>
public sealed class DeploymentRuntimeViewers(TimeProvider clock) : IDeploymentRuntimeViewers
{
    private const int MaximumLeases = 4096;
    private readonly object _gate = new();

    private readonly Dictionary<(string Connection, Guid Project), (Guid Deployment, DateTimeOffset Until)>
        _leases = [];

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Remove(string connectionId, Guid? projectId = null)
    {
        lock (_gate)
        {
            foreach (var key in _leases.Keys.Where(k => k.Connection == connectionId &&
                                                        (!projectId.HasValue || k.Project == projectId)).ToArray())
                _leases.Remove(key);
        }
    }

    /// <inheritdoc />
    public bool HasViewers(Guid projectId, Guid deploymentId)
    {
        lock (_gate)
        {
            Prune();
            return _leases.Any(p => p.Key.Project == projectId && p.Value.Deployment == deploymentId);
        }
    }

    /// <summary>Expires abandoned connections, including clients that never disconnect cleanly.</summary>
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _leases.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
            _leases.Remove(key);
    }
}