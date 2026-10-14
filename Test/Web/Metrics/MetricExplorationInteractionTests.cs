using Application.Abstractions.Diagnostics.Metrics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Web.Components.Shared.Metrics;

namespace Test.Web.Metrics;

#pragma warning disable BL0006 // Actual renderer exercises server event callbacks, without a browser fixture shim.
/// <summary>
///     Checks actual pagination events and preservation of complete chart input.
/// </summary>
public sealed class MetricExplorationInteractionTests
{
    /// <summary>
    ///     Next changes only table rows; resizing begins at page one and leaves plotted evidence
    ///     unchanged.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Pager_events_change_rows_without_restricting_chart_evidence()
    {
        await using var services = new ServiceCollection().AddSingleton<IJSRuntime, FixtureJs>().BuildServiceProvider();
        await using var renderer = new FixtureRenderer(services);
        var end = DateTimeOffset.UtcNow.AddSeconds(-1);
        var range = new MetricTimeRange(end.AddMinutes(-30), end);
        var rows = Enumerable.Range(0, 40).Select(i => new MetricObservation(Guid.NewGuid(), Guid.NewGuid(),
            "web-" + i, TelemetryPresentation.Cpu, "cores", range.Start.AddMinutes(i % 20), 1, .5, .1, .9)).ToArray();
        var id = await renderer.Dispatcher.InvokeAsync(() => renderer.AttachAsync(new MetricStatisticsView(),
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = rows, ["Range"] = range })));
        Assert.Equal(25, renderer.TableRows(id));
        Assert.Equal(20, renderer.ChartPoints(id));
        var pager = renderer.Child(id, typeof(StatisticsPager));
        await renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(
            renderer.Events(pager, "onclick").Last(),
            null, new MouseEventArgs()));
        Assert.Equal(15, renderer.TableRows(id));
        Assert.Equal(20, renderer.ChartPoints(id));
        await renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(
            renderer.Events(pager, "onchange").Single(),
            null, new ChangeEventArgs { Value = "10" }));
        Assert.Equal(10, renderer.TableRows(id));
        Assert.Equal(20, renderer.ChartPoints(id));
        Assert.Empty(renderer.Errors);
    }

    /// <summary>
    ///     No-op browser module permits chart lifecycle callbacks in the real renderer.
    /// </summary>
    private sealed class FixtureJs : IJSRuntime, IJSObjectReference
    {
        /// <summary>
        ///     Completes immediately without executing live dispose work.
        /// </summary>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>
        ///     Returns the fixture JS object reference for module requests and a default value for other
        ///     calls.
        /// </summary>
        /// <param name="identifier">Provider or user identity to resolve; ignored by this fixture.</param>
        /// <param name="args">
        ///     Arguments for the selected fixture port or reflected component member; ignored by this
        ///     fixture.
        /// </param>
        /// <typeparam name="TValue">Type of the requested interop result.</typeparam>
        /// <returns>A value task returning the configured fixture reference or default requested value.</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);
        }

        /// <summary>
        ///     Delegates to the fixture interop overload using the same identifier and arguments.
        /// </summary>
        /// <param name="identifier">Provider or user identity to resolve.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <typeparam name="TValue">Type of the requested interop result.</typeparam>
        /// <returns>A value task returning the configured fixture reference or default requested value.</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }

    /// <summary>
    ///     Minimal real renderer exposes produced frames and event identities for assertions.
    /// </summary>
    /// <param name="services">Fixture dependency provider.</param>
    private sealed class FixtureRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        /// <summary>Renderer dispatcher used to serialize fixture component updates.</summary>
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Unexpected component failures.</summary>
        public List<Exception> Errors { get; } = [];

        /// <summary>
        ///     Attaches the real component and waits for initial parameter rendering.
        /// </summary>
        /// <param name="component">Component rendered by the fixture.</param>
        /// <param name="parameters">Component parameters supplied by the rendering fixture.</param>
        /// <returns>A task returning the fixture count or numeric observation.</returns>
        public async Task<int> AttachAsync(IComponent component, ParameterView parameters)
        {
            var id = AssignRootComponentId(component);
            await RenderRootComponentAsync(id, parameters);
            return id;
        }

        /// <summary>
        ///     Returns only populated frames in the current render tree.
        /// </summary>
        /// <param name="id">Selected project identity.</param>
        /// <returns>The populated render-tree frames for the selected fixture component.</returns>
        private IEnumerable<RenderTreeFrame> Frames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return frames.Array.Take(frames.Count);
        }

        /// <summary>
        ///     Counts dynamic data rows; the constant header is emitted as a markup frame.
        /// </summary>
        /// <param name="id">Selected project identity.</param>
        /// <returns>The fixture count or numeric observation.</returns>
        public int TableRows(int id)
        {
            return Frames(id).Count(f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "tr");
        }

        /// <summary>
        ///     Finds a nested component instance's renderer identity.
        /// </summary>
        /// <param name="id">Selected project identity.</param>
        /// <param name="type">Fixture type or behavior variant.</param>
        /// <returns>The fixture count or numeric observation.</returns>
        public int Child(int id, Type type)
        {
            return Frames(id).First(f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == type)
                .ComponentId;
        }

        /// <summary>
        ///     Counts complete CPU chart input supplied by the parent.
        /// </summary>
        /// <param name="id">Selected project identity.</param>
        /// <returns>The fixture count or numeric observation.</returns>
        public int ChartPoints(int id)
        {
            return Frames(id).Where(f => f.FrameType == RenderTreeFrameType.Attribute &&
                                         f.AttributeName == "Points").Select(f =>
                ((IReadOnlyList<TelemetryChartPoint>)f.AttributeValue).Count).Max();
        }

        /// <summary>
        ///     Finds browser-dispatchable event handlers produced by the actual pager.
        /// </summary>
        /// <param name="id">Selected project identity.</param>
        /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
        /// <returns>Browser-dispatchable handler identifiers matching the selected event attribute.</returns>
        public IEnumerable<ulong> Events(int id, string name)
        {
            return Frames(id).Where(f => f.FrameType == RenderTreeFrameType.Attribute &&
                                         f.AttributeName == name).Select(f => f.AttributeEventHandlerId);
        }

        /// <summary>
        ///     Captures renderer failures for scenario assertions.
        /// </summary>
        /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
        protected override void HandleException(Exception exception)
        {
            Errors.Add(exception);
        }

        /// <summary>
        ///     Completes immediately without executing live update display work.
        /// </summary>
        /// <param name="renderBatch">Renderer update batch supplied by Blazor; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            return Task.CompletedTask;
        }
    }
}
#pragma warning restore BL0006
