using System.Reflection;
using System.Text.RegularExpressions;
using Application.Abstractions.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Real controls and numeric alternatives preserve UTC validation and independent pagination.</summary>
public sealed class MetricExplorationRenderingTests
{
    /// <summary>All chart choices are present and numeric rows are bounded independently of plotted evidence.</summary>
    [Fact]
    public async Task Picker_and_statistics_render_all_ranges_and_paginate_without_limiting_charts()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime>(DispatchProxy.Create<IJSRuntime, ConsoleRenderingTests.EmptyPort>())
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var picker = (await renderer.RenderComponentAsync<MetricRangePicker>()).ToHtmlString();
            foreach (var value in new[] { "10m", "30m", "1h", "6h", "24h", "7d", "30d", "90d", "1y", "5y", "custom" })
                Assert.Contains($"value=\"{value}\"", picker);
            var end = DateTimeOffset.UtcNow.AddSeconds(-1);
            var range = new MetricTimeRange(end.AddMinutes(-30), end);
            var rows = Enumerable.Range(0, 40).Select(i => new MetricObservation(Guid.NewGuid(), Guid.NewGuid(),
                "web-" + i, TelemetryPresentation.Cpu, "cores", range.Start.AddMinutes(i % 20),
                1, .5, .1, .9)).ToArray();
            var root = await renderer.RenderComponentAsync<MetricStatisticsView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Data"] = rows, ["Range"] = range }));
            var html = root.ToHtmlString();
            Assert.Equal(25, Regex.Matches(html, "<tr><td>").Count);
            Assert.Equal(20, Regex.Matches(html, "class=\"telemetry-point\"").Count);
            Assert.Contains("Page 1 of 2", html);
            Assert.Contains("40 rows", html);
            Assert.Contains("25", html);
        });
    }

    /// <summary>Custom UTC validation rejects future/short windows; five-minute windows publish once.</summary>
    [Fact]
    public async Task Custom_validation_and_lifetime_clipping_publish_only_valid_frozen_ranges()
    {
        var published = new List<MetricTimeRange>();
        var picker = new MetricRangePicker();
        ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Changed"] =
                    EventCallback.Factory.Create(published, (MetricTimeRange r) => published.Add(r))
            })
            .SetParameterProperties(picker);
        var end = DateTimeOffset.UtcNow.AddMinutes(-1).UtcDateTime;
        Set("_choice", "custom");
        Set("_end", end);
        Set("_start", end.AddMinutes(-4));
        await Apply();
        Assert.Empty(published);
        Set("_start", end.AddMinutes(-5));
        await Apply();
        Assert.Single(published);
        Assert.Null(Get("_notice"));
        Assert.Equal(TimeSpan.FromMinutes(5), published[0].End - published[0].Start);
        Set("_end", DateTime.UtcNow.AddHours(1));
        await Apply();
        Assert.Single(published);
        var lifetimeEnd = DateTimeOffset.UtcNow.AddDays(-1);
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["LifetimeStart"] = lifetimeEnd.AddYears(-7),
            ["LifetimeEnd"] = lifetimeEnd
        }).SetParameterProperties(picker);
        Set("_choice", "lifetime");
        await Apply();
        Assert.Equal(lifetimeEnd.AddYears(-5), published[1].Start);
        Assert.Contains("truncated", (string)Get("_notice")!);

        void Set(string name, object value)
        {
            typeof(MetricRangePicker).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(picker,
                value);
        }

        object? Get(string name)
        {
            return typeof(MetricRangePicker).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(picker);
        }

        Task Apply()
        {
            return (Task)typeof(MetricRangePicker).GetMethod("ApplyAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null)!;
        }
    }
}