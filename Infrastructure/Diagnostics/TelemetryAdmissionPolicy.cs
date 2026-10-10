using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Diagnostics;

/// <summary>Short-lived, bounded server-owned identity mapping, never derived from a tenant HTTP header.</summary>
public sealed class TelemetryAdmissionPolicy(IServiceScopeFactory scopes) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 4096 });

    public void Dispose()
    {
        _cache.Dispose();
    }

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