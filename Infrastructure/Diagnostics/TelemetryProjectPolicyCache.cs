using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Diagnostics;

public sealed record TelemetryProjectPolicy(Guid UserId, bool RuntimeDiagnosticsEnabled, bool ManagedTelemetryConsent);

/// <summary>Bounds control-plane reads; collection/consent changes become effective within five seconds.</summary>
public sealed class TelemetryProjectPolicyCache(IServiceScopeFactory scopes) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 4096 });

    public void Dispose()
    {
        _cache.Dispose();
    }

    public async Task<TelemetryProjectPolicy?> GetAsync(Guid project, CancellationToken token)
    {
        if (_cache.TryGetValue(project, out TelemetryProjectPolicy? value)) return value;
        await using var scope = scopes.CreateAsyncScope();
        value = await scope.ServiceProvider.GetRequiredService<AutoMateDbContext>().Applications.AsNoTracking()
            .Where(p => p.Id == project).Select(p => new TelemetryProjectPolicy(p.UserId,
                p.RuntimeDiagnosticsEnabled, p.ManagedTelemetryConsent)).SingleOrDefaultAsync(token);
        _cache.Set(project, value,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5), Size = 1 });
        return value;
    }
}