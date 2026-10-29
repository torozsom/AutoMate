using Application.Abstractions.Diagnostics.Metrics;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Web.Components.Shared.Metrics;

namespace Test.Web.Metrics;

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
        await using var context = new BunitContext();
        context.Services.AddSingleton<IJSRuntime, FixtureJs>();
        var end = DateTimeOffset.UtcNow.AddSeconds(-1);
        var range = new MetricTimeRange(end.AddMinutes(-30), end);
        var rows = Enumerable.Range(0, 40)
            .Select(i => new MetricObservation(Guid.NewGuid(), Guid.NewGuid(),
                "web-" + i, TelemetryPresentation.Cpu, "cores", range.Start.AddMinutes(i % 20), 1, .5, .1, .9))
            .ToArray();
        var rendered = context.Render<MetricStatisticsView>(parameters => parameters
            .Add(component => component.Data, rows)
            .Add(component => component.Range, range));

        Assert.Equal(25, rendered.FindAll("tbody tr").Count);
        Assert.Contains("Page 1 of 2", rendered.Find(".statistics-pager [role='status']").TextContent);
        AssertChartEvidence();

        await rendered.Find(".statistics-pager button:last-of-type").ClickAsync(new MouseEventArgs());

        Assert.Equal(15, rendered.FindAll("tbody tr").Count);
        Assert.Contains("Page 2 of 2", rendered.Find(".statistics-pager [role='status']").TextContent);
        AssertChartEvidence();

        await rendered.Find(".statistics-pager select").ChangeAsync(new ChangeEventArgs { Value = "10" });

        Assert.Equal(10, rendered.FindAll("tbody tr").Count);
        Assert.Contains("Page 1 of 4", rendered.Find(".statistics-pager [role='status']").TextContent);
        AssertChartEvidence();
        Assert.False(context.Renderer.UnhandledException.IsCompleted);

        // Chart input remains complete while the numeric table changes page or page size.
        void AssertChartEvidence()
        {
            var cpu = Assert.Single(rendered.FindComponents<TelemetryChart>(),
                chart => chart.Instance.Metric == TelemetryPresentation.Cpu);
            Assert.Equal(20, cpu.Instance.Points.Count);
        }
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
            return ValueTask.FromResult<TValue>(typeof(TValue) == typeof(IJSObjectReference)
                ? (TValue)(object)this
                : default!);
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
}
