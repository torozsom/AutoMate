using System.Collections.Concurrent;
using System.Reflection;
using Application.Abstractions.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Components.Shared.Metrics;

namespace Test.Web.Metrics;

/// <summary>
///     Analytics queries must not share the parent Blazor circuit's scoped database services.
/// </summary>
public sealed class ProjectTelemetrySummaryTests
{
    /// <summary>
    ///     Verifies that overlapping analytics loads use and dispose independent scopes.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Overlapping_analytics_loads_use_and_dispose_independent_scopes()
    {
        var recorder = new ScopeRecorder();
        await using var services = new ServiceCollection().AddSingleton(recorder)
            .AddScoped<IProjectTelemetryAnalytics, ScopedAnalytics>().BuildServiceProvider();
        var component = new ProjectTelemetrySummary();
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();

        Set(component, "UserId", owner);
        Set(component, "ProjectId", project);
        Set(component, "ServiceScopes", services.GetRequiredService<IServiceScopeFactory>());
        Set(component, "Logger", NullLogger<ProjectTelemetrySummary>.Instance);
        var load = typeof(ProjectTelemetrySummary).GetMethod("LoadAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await Task.WhenAll((Task)load.Invoke(component, null)!, (Task)load.Invoke(component, null)!);

        Assert.Equal(2, recorder.Calls.Count);
        Assert.Equal(2, recorder.Calls.Select(call => call.Scope).Distinct().Count());
        foreach (var call in recorder.Calls)
        {
            Assert.Equal(owner, call.Owner);
            Assert.Equal(project, call.Project);
        }

        Assert.Equal(2, recorder.Disposed.Count);

        component.Dispose();
    }

    /// <summary>
    ///     Updates the selected fixture component member through reflection.
    /// </summary>
    /// <param name="component">Component instance accessed by the fixture.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <param name="value">Fixture member value to assign.</param>
    private static void Set(object component, string name, object value)
    {
        component.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(component, value);
    }

    /// <summary>
    ///     Tracks scoped analytics calls and disposal identities for concurrency assertions.
    /// </summary>
    private sealed class ScopeRecorder
    {
        /// <summary>Captured fixture calls or scope/owner/project observations.</summary>
        public ConcurrentBag<(Guid Scope, Guid Owner, Guid Project)> Calls { get; } = [];

        /// <summary>Whether fixture resource disposal has been observed.</summary>
        public ConcurrentBag<Guid> Disposed { get; } = [];
    }

    /// <summary>
    ///     Records its scope identity and returns delayed empty analytics without an external database.
    /// </summary>
    /// <param name="recorder">Shared recorder for independent service scopes.</param>
    private sealed class ScopedAnalytics(ScopeRecorder recorder) : IProjectTelemetryAnalytics, IDisposable
    {
        /// <summary>Identity of this scoped analytics fake.</summary>
        private readonly Guid _id = Guid.NewGuid();

        /// <summary>
        ///     Records disposal of this analytics scope.
        /// </summary>
        public void Dispose()
        {
            recorder.Disposed.Add(_id);
        }

        /// <summary>
        ///     Records owner/project/scope identity and returns empty analytics after a cancellable fixture
        ///     delay.
        /// </summary>
        /// <param name="user">Synthetic owner identity.</param>
        /// <param name="project">Synthetic project identity.</param>
        /// <param name="start">UTC observation-window start; ignored by this fixture.</param>
        /// <param name="end">UTC observation-window end; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the configured project telemetry analytics instance or response for this scenario.</returns>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public async Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start,
            DateTimeOffset end, CancellationToken token = default)
        {
            recorder.Calls.Add((_id, user, project));
            await Task.Delay(20, token);

            return new ProjectTelemetryAnalytics(0, 0, 0, null, [], null);
        }
    }
}
