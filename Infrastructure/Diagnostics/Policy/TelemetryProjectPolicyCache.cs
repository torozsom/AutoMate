using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Diagnostics;

/// <summary>
///     Detached project ownership and compatibility flags used by telemetry admission checks.
/// </summary>
/// <param name="UserId">The internal AutoMate user identity.</param>
/// <param name="RuntimeDiagnosticsEnabled">
///     Retained compatibility flag; active runtime collection is automatic.
/// </param>
/// <param name="ManagedTelemetryConsent">
///     Retained compatibility flag; operator-approved telemetry storage is automatic.
/// </param>
public sealed record TelemetryProjectPolicy(Guid UserId, bool RuntimeDiagnosticsEnabled, bool ManagedTelemetryConsent);

/// <summary>
///     Bounds control-plane reads; ownership changes become effective within five seconds.
/// </summary>
/// <param name="scopes">Creates independent scopes for concurrent background work.</param>
public sealed class TelemetryProjectPolicyCache(IServiceScopeFactory scopes) : IDisposable
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
    ///     Reads bounded cached owner/policy metadata without retaining diagnostic payloads.
    /// </summary>
    /// <param name="project">The owned project identity or normalized local import metadata.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning detached project ownership and operator processing policy, or null when
    ///     no value is available.
    /// </returns>
    public async Task<TelemetryProjectPolicy?> GetAsync(Guid project, CancellationToken token)
    {
        if (_cache.TryGetValue(project, out TelemetryProjectPolicy? value)) return value;
        await using var scope = scopes.CreateAsyncScope();
        value = await scope.ServiceProvider.GetRequiredService<AutoMateDbContext>().Applications.AsNoTracking()
            .Where(p => p.Id == project).Select(p => new TelemetryProjectPolicy(p.UserId,
                true, true)).SingleOrDefaultAsync(token);
        _cache.Set(project, value,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5), Size = 1 });
        return value;
    }
}
