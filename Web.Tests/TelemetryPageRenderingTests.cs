using System.Reflection;
using System.Security.Claims;
using Application.Abstractions.Diagnostics;
using Application.Data.Users;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Web.Components.Pages;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Renders both actual telemetry surfaces with explicit provider fixtures and no external authentication.</summary>
public sealed class TelemetryPageRenderingTests
{
    /// <summary>Produces browser-reviewable HTML using the actual page and summary components.</summary>
    [Fact]
    public async Task Summary_and_history_render_compact_cards_and_collapsed_details()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime, StaticJs>().AddSingleton<AuthenticationStateProvider, FixtureAuthentication>()
            .AddSingleton(DispatchProxy.Create<IUserService, UnusedUserService>())
            .AddSingleton<IDeploymentHistoryService, FixtureHistory>().AddSingleton<IProjectTelemetryAnalytics, FixtureAnalytics>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var project = await Render<ProjectTelemetrySummary>(renderer, new() { ["ProjectId"] = Guid.NewGuid(), ["UserId"] = Guid.NewGuid() });
        var history = await Render<DeploymentHistory>(renderer, new() { ["ProjectId"] = Guid.NewGuid(), ["DeploymentId"] = Guid.NewGuid() });
        Assert.Contains("Per observed container", project);
        Assert.Contains("Resource overview", history);
        Assert.Contains("Collection settings", history);
        Assert.DoesNotContain("<details open", history);
        Assert.DoesNotContain("E+", history);
        var root = FindRoot();
        var directory = Path.Combine(root, ".artifacts", "metrics-preview");
        Directory.CreateDirectory(directory);
        var css = new[] { Path.Combine(root, "Web", "obj"), Path.Combine(root, ".artifacts") }.Where(Directory.Exists).SelectMany(path => Directory.GetFiles(path, "Web.styles.css", SearchOption.AllDirectories)).Where(path => path.Replace('\\', '/').Contains("/scopedcss/bundle/")).OrderByDescending(File.GetLastWriteTimeUtc).First();
        File.Copy(css, Path.Combine(directory, "Web.styles.css"), true);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "lib", "bootstrap", "dist", "css", "bootstrap.min.css"), Path.Combine(directory, "bootstrap.css"), true);
        foreach (var page in new[] { (Name: "project", Html: project), (Name: "history", Html: history) })
            await File.WriteAllTextAsync(Path.Combine(directory, page.Name + ".html"), Wrap(page.Html));
    }

    /// <summary>Renders a real component through its normal parameter lifecycle.</summary>
    private static Task<string> Render<T>(HtmlRenderer renderer, Dictionary<string, object?> parameters) where T : IComponent =>
        renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());

    /// <summary>Adds local theme styles and client inspection to a clearly identified provider-fixture preview.</summary>
    private static string Wrap(string content) => "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><link rel='stylesheet' href='/bootstrap.css'><link rel='stylesheet' href='/app.css'><link rel='stylesheet' href='/Web.styles.css'><link rel='stylesheet' href='/telemetry.css'></head><body><div style='max-width:1120px;margin:auto;padding:16px'><small>UI verification · example provider data</small><button style='margin-left:16px' onclick=\"document.documentElement.dataset.bsTheme=document.documentElement.dataset.bsTheme==='dark'?'light':'dark'\">Toggle theme</button><button onclick=\"document.documentElement.style.fontSize='200%'\">200% text</button>" + content + "</div><script type='module'>import {attach} from '/js/telemetry-chart.js';document.querySelectorAll('.telemetry-chart').forEach(attach);</script></body></html>";

    /// <summary>Finds the repository independently of test-output configuration.</summary>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutoMate.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>Guid claims exercise normal owner resolution without invoking any remote identity provider.</summary>
    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "fixture"))));
    }

    /// <summary>Any unexpected user lookup fails this credential-free fixture.</summary>
    public class UnusedUserService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Unexpected user lookup");
    }

    /// <summary>Static rendering must not try to execute JavaScript.</summary>
    private sealed class StaticJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("Unexpected interop");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<T>(identifier, args);
    }

    /// <summary>Representative sparse history, with the same resource magnitudes as the reported screenshots.</summary>
    private sealed class FixtureHistory : IDeploymentHistoryService
    {
        public Task<DeploymentTerminalHistory> ReadLogsAsync(Guid user, Guid project, Guid deployment, long cursor, bool backwards, int limit, CancellationToken token = default) =>
            Task.FromResult(new DeploymentTerminalHistory([new DeploymentTerminalLog(1, project, deployment, "build", "Build completed")], false));
        public Task<DeploymentMetricHistory> ReadMetricsAsync(Guid user, Guid project, Guid deployment, DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken token = default) =>
            Task.FromResult(new DeploymentMetricHistory([
                new("web", TelemetryPresentation.Cpu, "cores", end.AddHours(-1), .006, 0, .012),
                new("web", TelemetryPresentation.Memory, "bytes", end.AddHours(-1), 19430000, 14156000, 24704000),
                new("web", TelemetryPresentation.MemoryLimit, "bytes", end.AddHours(-1), 32490000000, 32490000000, 32490000000)
            ], "Some intervals contain missing observations."));
        public Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid user, Guid project, CancellationToken token = default) => Task.FromResult(new DeploymentTelemetryPreferences(false, false, false, "local"));
        public Task SetManagedConsentAsync(Guid user, Guid project, bool enabled, CancellationToken token = default) => throw new NotSupportedException();
        public Task SetRuntimeCollectionAsync(Guid user, Guid project, bool enabled, CancellationToken token = default) => throw new NotSupportedException();
    }

    /// <summary>Representative daily aggregate response preserves supplied sample counts and quality flags.</summary>
    private sealed class FixtureAnalytics : IProjectTelemetryAnalytics
    {
        public Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start, DateTimeOffset end, CancellationToken token = default) =>
            Task.FromResult(new ProjectTelemetryAnalytics(1, 1, 0, null, [
                new(Guid.NewGuid(), new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero), "web", TelemetryPresentation.Cpu, "cores", 2, .006, 0, .012, 0, true, end),
                new(Guid.NewGuid(), new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero), "web", TelemetryPresentation.Memory, "bytes", 2, 19430000, 14156000, 24704000, 0, true, end)
            ], "Some daily statistics contain missing data."));
    }
}
