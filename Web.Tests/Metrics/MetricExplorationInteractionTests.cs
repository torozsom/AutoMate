using Application.Abstractions.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

#pragma warning disable BL0006 // Actual renderer exercises server event callbacks, without a browser fixture shim.
/// <summary>Checks actual pagination events and preservation of complete chart input.</summary>
public sealed class MetricExplorationInteractionTests
{
    /// <summary>Next changes only table rows; resizing begins at page one and leaves plotted evidence unchanged.</summary>
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

    /// <summary>No-op browser module permits chart lifecycle callbacks in the real renderer.</summary>
    private sealed class FixtureJs : IJSRuntime, IJSObjectReference
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);
        }

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }

    /// <summary>Minimal real renderer exposes produced frames and event identities for assertions.</summary>
    private sealed class FixtureRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        /// <inheritdoc />
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Unexpected component failures.</summary>
        public List<Exception> Errors { get; } = [];

        /// <summary>Attaches the real component and waits for initial parameter rendering.</summary>
        public async Task<int> AttachAsync(IComponent component, ParameterView parameters)
        {
            var id = AssignRootComponentId(component);
            await RenderRootComponentAsync(id, parameters);
            return id;
        }

        /// <summary>Returns only populated frames in the current render tree.</summary>
        private IEnumerable<RenderTreeFrame> Frames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return frames.Array.Take(frames.Count);
        }

        /// <summary>Counts dynamic data rows; the constant header is emitted as a markup frame.</summary>
        public int TableRows(int id)
        {
            return Frames(id).Count(f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "tr");
        }

        /// <summary>Finds a nested component instance's renderer identity.</summary>
        public int Child(int id, Type type)
        {
            return Frames(id).First(f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == type)
                .ComponentId;
        }

        /// <summary>Counts complete CPU chart input supplied by the parent.</summary>
        public int ChartPoints(int id)
        {
            return Frames(id).Where(f => f.FrameType == RenderTreeFrameType.Attribute &&
                                         f.AttributeName == "Points").Select(f =>
                ((IReadOnlyList<TelemetryChartPoint>)f.AttributeValue).Count).Max();
        }

        /// <summary>Finds browser-dispatchable event handlers produced by the actual pager.</summary>
        public IEnumerable<ulong> Events(int id, string name)
        {
            return Frames(id).Where(f => f.FrameType == RenderTreeFrameType.Attribute &&
                                         f.AttributeName == name).Select(f => f.AttributeEventHandlerId);
        }

        /// <inheritdoc />
        protected override void HandleException(Exception exception)
        {
            Errors.Add(exception);
        }

        /// <inheritdoc />
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            return Task.CompletedTask;
        }
    }
}
#pragma warning restore BL0006