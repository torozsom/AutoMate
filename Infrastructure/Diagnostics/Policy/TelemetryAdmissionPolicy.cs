using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Diagnostics;

/// <summary>
///     Short-lived, bounded server-owned identity mapping, never derived from a tenant HTTP
///     header.
/// </summary>
/// <param name="scopes">Creates independent scopes for concurrent background work.</param>
public sealed class TelemetryAdmissionPolicy(IServiceScopeFactory scopes) : IDisposable
{
    /// <summary>Bounded cache of non-payload ownership or policy metadata.</summary>
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 4096 });

    /// <summary>
    ///     Releases owned resources and completes the documented service lifetime.
    /// </summary>
    public void Dispose()
    {
        _cache.Dispose();
    }

    /// <summary>
    ///     Resolves current project ownership and operator-approved processing policy for
    ///     ingestion.
    /// </summary>
    /// <param name="project">The owned project identity or normalized local import metadata.</param>
    /// <param name="deployment">The exact deployment identity or tracked metadata for this operation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning detached project ownership and operator processing policy, or null when
    ///     no value is available.
    /// </returns>
    public async Task<TelemetryProjectPolicy?> GetAsync(Guid project, Guid? deployment, CancellationToken token)
    {
        var key = (project, deployment);
        if (_cache.TryGetValue(key, out TelemetryProjectPolicy? value)) return value;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        value = await db.Applications.AsNoTracking().Where(p => p.Id == project &&
                                                                (deployment == null || db.Deployments.Any(d =>
                                                                    d.Id == deployment &&
                                                                    d.CsProject!.AppId == project)))
            .Select(p => new TelemetryProjectPolicy(p.UserId, true, true))
            .SingleOrDefaultAsync(token);
        _cache.Set(key, value,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5) });
        return value;
    }
}
