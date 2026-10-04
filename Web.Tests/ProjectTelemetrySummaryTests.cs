using System.Collections.Concurrent;
using System.Reflection;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Analytics queries must not share the parent Blazor circuit's scoped database services.</summary>
public sealed class ProjectTelemetrySummaryTests
{
    [Fact]
    public async Task Overlapping_analytics_loads_use_and_dispose_independent_scopes()
    {
        var recorder = new ScopeRecorder();
        using var services = new ServiceCollection().AddSingleton(recorder)
            .AddScoped<IProjectTelemetryAnalytics, ScopedAnalytics>().BuildServiceProvider();
        var component = new ProjectTelemetrySummary();
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();
        Set(component, "UserId", owner);
        Set(component, "ProjectId", project);
        Set(component, "ServiceScopes", services.GetRequiredService<IServiceScopeFactory>());
        Set(component, "Logger", NullLogger<ProjectTelemetrySummary>.Instance);
        var load = typeof(ProjectTelemetrySummary).GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await Task.WhenAll((Task)load.Invoke(component, null)!, (Task)load.Invoke(component, null)!);
        Assert.Equal(2, recorder.Calls.Count);
        Assert.Equal(2, recorder.Calls.Select(call => call.Scope).Distinct().Count());
        Assert.All(recorder.Calls, call => { Assert.Equal(owner, call.Owner); Assert.Equal(project, call.Project); });
        Assert.Equal(2, recorder.Disposed.Count);
        component.Dispose();
    }

    private static void Set(object component, string name, object value) =>
        component.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(component, value);

    private sealed class ScopeRecorder
    {
        public ConcurrentBag<(Guid Scope, Guid Owner, Guid Project)> Calls { get; } = [];
        public ConcurrentBag<Guid> Disposed { get; } = [];
    }

    private sealed class ScopedAnalytics(ScopeRecorder recorder) : IProjectTelemetryAnalytics, IDisposable
    {
        private readonly Guid _id = Guid.NewGuid();

        public async Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start,
            DateTimeOffset end, CancellationToken token = default)
        {
            recorder.Calls.Add((_id, user, project));
            await Task.Delay(20, token);
            return new ProjectTelemetryAnalytics(0, 0, 0, null, [], null);
        }

        public void Dispose() => recorder.Disposed.Add(_id);
    }
}
