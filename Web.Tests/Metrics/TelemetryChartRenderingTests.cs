using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Checks the rendered chart for sparse and missing observations, not just its calculations.</summary>
public sealed class TelemetryChartRenderingTests
{
    /// <summary>A single observation remains visible and has a meaningful accessible explanation.</summary>
    [Fact]
    public async Task Single_point_has_marker_and_keyboard_details()
    {
        var now = DateTimeOffset.UtcNow;
        var html = await RenderAsync([new TelemetryChartPoint(now.AddHours(-1), .006, 0, .012)], now);
        Assert.Contains("<circle", html);
        Assert.Contains("data-detail=", html);
        Assert.Contains("tabindex=\"0\"", html);
        Assert.Contains("insufficient data for a trend", html);
        Assert.DoesNotContain("class=\"telemetry-series-line\"", html);
    }

    /// <summary>Missing intervals break the plotted line; contiguous constant observations still connect.</summary>
    [Fact]
    public async Task Gaps_are_not_connected_and_constant_points_are_visible()
    {
        var now = DateTimeOffset.UtcNow;
        var html = await RenderAsync(
        [
            new TelemetryChartPoint(now.AddDays(-4), .006), new TelemetryChartPoint(now.AddDays(-3), .006),
            new TelemetryChartPoint(now, 0)
        ], now);
        Assert.Equal(1, html.Split("class=\"telemetry-series-line\"").Length - 1);
        Assert.Equal(3, html.Split("<circle").Length - 1);
    }

    /// <summary>Writes a dependency-free preview of the actual chart component for browser inspection.</summary>
    [Fact]
    public async Task Render_representative_browser_preview()
    {
        var now = DateTimeOffset.UtcNow;
        var sparse = await RenderAsync([new TelemetryChartPoint(now.AddDays(-1), .006, 0, .012)], now);
        var populated =
            await RenderAsync(
                Enumerable.Range(0, 7)
                    .Select(i => new TelemetryChartPoint(now.AddDays(i - 7), .002 + .001 * (i % 3), 0, .012)).ToArray(),
                now);
        var root = FindRoot();
        var directory = Path.Combine(root, ".artifacts", "metrics-preview");
        Directory.CreateDirectory(directory);
        var html =
            "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'><link rel='stylesheet' href='/app.css'><link rel='stylesheet' href='/telemetry.css'></head><body style='padding:24px;max-width:1120px;margin:auto'><h1>Telemetry chart verification</h1><p>Sparse and populated example observations</p><button onclick=\"document.documentElement.dataset.bsTheme=document.documentElement.dataset.bsTheme==='dark'?'light':'dark'\">Toggle theme</button><section class='telemetry-panel'><div class='telemetry-kpis'><div class='telemetry-kpi'><span>Average CPU</span><strong>0.006 cores</strong><small>Per observed container</small></div><div class='telemetry-kpi'><span>Average memory</span><strong>18.53 MiB</strong><small>Per observed container</small></div></div><details class='telemetry-explore'><summary>Explore metrics</summary><div class='telemetry-chart-grid'>" +
            sparse + populated +
            "</div></details></section><script type='module'>import {attach} from '/js/telemetry-chart.js';document.querySelectorAll('.telemetry-chart').forEach(attach);</script></body></html>";
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), html);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "app.css"), Path.Combine(directory, "app.css"), true);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "telemetry.css"), Path.Combine(directory, "telemetry.css"),
            true);
        Directory.CreateDirectory(Path.Combine(directory, "js"));
        File.Copy(Path.Combine(root, "Web", "wwwroot", "js", "telemetry-chart.js"),
            Path.Combine(directory, "js", "telemetry-chart.js"), true);
        Assert.Contains("telemetry-chart", html);
    }

    /// <summary>Renders the real component without running browser interop or external providers.</summary>
    private static async Task<string> RenderAsync(IReadOnlyList<TelemetryChartPoint> points, DateTimeOffset end)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoBrowserJs>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TelemetryChart>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Title"] = "CPU usage", ["Metric"] = TelemetryPresentation.Cpu,
                    ["Points"] = points, ["Start"] = end.AddDays(-7), ["End"] = end, ["Gap"] = TimeSpan.FromDays(1)
                }));
            return component.ToHtmlString();
        });
    }

    /// <summary>Finds the solution regardless of the chosen test output directory.</summary>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutoMate.slnx")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>Static HTML rendering never invokes JavaScript.</summary>
    private sealed class NoBrowserJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            throw new InvalidOperationException("Unexpected prerender interop");
        }

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            return InvokeAsync<T>(identifier, args);
        }
    }
}