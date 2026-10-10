using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Application.Data.Apps;
using Application.Data.Users;
using Domain.Entities;
using Domain.Enums;
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
        var registrations = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime, StaticJs>().AddSingleton<AuthenticationStateProvider, FixtureAuthentication>()
            .AddSingleton(DispatchProxy.Create<IUserService, UnusedUserService>())
            .AddSingleton<IDeploymentHistoryService, FixtureHistory>()
            .AddSingleton<IProjectTelemetryAnalytics, FixtureAnalytics>()
            .AddSingleton<IDeploymentDetailsService, FixtureDetails>()
            .AddSingleton(DispatchProxy.Create<IDeploymentAnalysisService, FixtureAssessments>())
            .AddSingleton<NavigationManager, FixtureNavigation>();
        foreach (var property in typeof(ProjectDetails).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                     .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null && p.PropertyType.IsInterface &&
                                 !p.PropertyType.IsGenericType))
            if (!registrations.Any(r => r.ServiceType == property.PropertyType))
                registrations.AddSingleton(property.PropertyType,
                    DispatchProxy.Create(property.PropertyType, typeof(EmptyPort)));
        using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var project = await Render<ProjectTelemetrySummary>(renderer,
            new Dictionary<string, object?> { ["ProjectId"] = Guid.NewGuid(), ["UserId"] = Guid.NewGuid() });
        var history = await Render<DeploymentHistory>(renderer,
            new Dictionary<string, object?> { ["ProjectId"] = Guid.NewGuid(), ["DeploymentId"] = Guid.NewGuid() });
        Assert.Contains("Per observed container", Regex.Replace(project, @"\s+", " "));
        var details = await Render<FixtureProject>(renderer,
            new Dictionary<string, object?> { ["ProjectId"] = Guid.NewGuid() });
        var anchors = new[]
            { "overview", "logs", "metrics", "analytics", "ai-analysis", "configuration", "deployments" };
        var previous = -1;
        foreach (var anchor in anchors)
        {
            var position = details.IndexOf($"id=\"{anchor}\"", StringComparison.Ordinal);
            Assert.True(position > previous);
            Assert.Contains($"href=\"#{anchor}\"", details);
            previous = position;
        }

        Assert.DoesNotContain("_notice", details);
        Assert.DoesNotContain("_metricAvailability", history);
        Assert.Contains("Total deployments", details);
        Assert.Contains("View Details", details);
        Assert.Contains("/actions/runs/42", details);
        Assert.Contains("Saved to dashboard", details);
        Assert.Contains("This deployment predates", await Render<DeploymentConfigurationDetails>(renderer,
            new Dictionary<string, object?> { ["Deployment"] = new Deployment() }));
        Assert.Contains("Resource overview", history);
        Assert.Contains("Collection settings", history);
        Assert.DoesNotContain("<details open", history);
        Assert.DoesNotContain("E+", history);
        var root = FindRoot();
        var directory = Path.Combine(root, ".artifacts", "metrics-preview");
        Directory.CreateDirectory(directory);
        var css = new[] { Path.Combine(root, "Web", "obj"), Path.Combine(root, ".artifacts") }.Where(Directory.Exists)
            .SelectMany(path => Directory.GetFiles(path, "Web.styles.css", SearchOption.AllDirectories))
            .Where(path => path.Replace('\\', '/').Contains("/scopedcss/bundle/"))
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        File.Copy(css, Path.Combine(directory, "Web.styles.css"), true);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "lib", "bootstrap", "dist", "css", "bootstrap.min.css"),
            Path.Combine(directory, "bootstrap.css"), true);
        foreach (var page in new[]
                 {
                     (Name: "project", Html: project), (Name: "history", Html: history),
                     (Name: "details", Html: details)
                 })
            await File.WriteAllTextAsync(Path.Combine(directory, page.Name + ".html"), Wrap(page.Html));
    }

    /// <summary>Renders a real component through its normal parameter lifecycle.</summary>
    private static Task<string> Render<T>(HtmlRenderer renderer, Dictionary<string, object?> parameters)
        where T : IComponent
    {
        return renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    /// <summary>Adds local theme styles and client inspection to a clearly identified provider-fixture preview.</summary>
    private static string Wrap(string content)
    {
        return
            "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><link rel='stylesheet' href='/bootstrap.css'><link rel='stylesheet' href='/app.css'><link rel='stylesheet' href='/Web.styles.css'><link rel='stylesheet' href='/telemetry.css'><link rel='stylesheet' href='/console.css'></head><body><div style='max-width:1120px;margin:auto;padding:16px'><small>UI verification · example provider data</small><button style='margin-left:16px' onclick=\"document.documentElement.dataset.bsTheme=document.documentElement.dataset.bsTheme==='dark'?'light':'dark'\">Toggle theme</button><button onclick=\"document.documentElement.style.fontSize='200%'\">200% text</button>" +
            content +
            "</div><script type='module'>import {attach} from '/js/telemetry-chart.js';document.querySelectorAll('.telemetry-chart').forEach(attach);import {attach as attachSections} from '/js/project-sections.js';document.querySelectorAll('.project-section-tabs').forEach(attachSections);</script></body></html>";
    }

    /// <summary>Finds the repository independently of test-output configuration.</summary>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutoMate.slnx")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>Renders the real project markup without authentication/provider lifecycle side effects.</summary>
    public sealed class FixtureProject : ProjectDetails
    {
        /// <inheritdoc />
        protected override Task OnInitializedAsync()
        {
            var project = new CsProject
                { Name = "Web", IsWebProject = true, Configuration = new Configuration { DotNetVersion = "10.0" } };
            var app = new Domain.Entities.Application
            {
                Name = "History demo",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-20),
                SourceType = SourceType.Remote,
                SourcePathOrUrl = "https://github.com/example/history-demo",
                CsProjects = [project]
            };
            project.Application = app;
            project.Deployments = Enumerable.Range(0, 8).Select(i => new Deployment
            {
                CsProject = project,
                CsProjectId = project.Id,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-8 + i),
                Status = i == 7 ? DeploymentStatus.Running : DeploymentStatus.Stopped,
                Outcome = DeploymentOutcome.Succeeded,
                CloudGitHubActionRunId = 42,
                CloudAppUrl = "https://example.invalid",
                ConfigurationSnapshotJson = JsonSerializer.Serialize(new DeploymentConfigurationSnapshot("Web",
                    "Azure Container Apps",
                    app.SourcePathOrUrl, "main", "abc123", "Production", "10.0", 8080, true,
                    "swedencentral", "demo-rg", "demo-app", "demo.azurecr.io"))
            }).ToList();
            typeof(ProjectDetails).GetField("_app", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, app);
            typeof(ProjectDetails).GetField("_isLoading", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, false);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Provider-free metadata for the selected historical route.</summary>
    private sealed class FixtureDetails : IDeploymentDetailsService
    {
        /// <inheritdoc />
        public Task<Deployment?> GetAsync(Guid owner, Guid project, Guid deployment, CancellationToken token = default)
        {
            return Task.FromResult<Deployment?>(new Deployment
            {
                Id = deployment,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                Status = DeploymentStatus.Stopped,
                Outcome = DeploymentOutcome.Succeeded,
                CsProject = new CsProject { Name = "Historical Web" },
                CloudGitHubActionRunId = 42,
                ConfigurationSnapshotJson = JsonSerializer.Serialize(new DeploymentConfigurationSnapshot(
                    "Historical Web", "Azure Container Apps", "https://github.com/example/history-demo", "release",
                    "abc123",
                    "Production", "10.0", 8080, true, "swedencentral", "historical-rg", "historical-app",
                    "demo.azurecr.io"))
            });
        }
    }

    /// <summary>Unused injected operations return bounded empty fixture data, never perform provider work.</summary>
    public class EmptyPort : DispatchProxy
    {
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var type = method!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GenericTypeArguments[0];
                var value = result.IsValueType ? Activator.CreateInstance(result) : null;
                if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                    value = Activator.CreateInstance(typeof(List<>).MakeGenericType(result.GenericTypeArguments));
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    /// <summary>Only local URLs are needed by the static project fixture.</summary>
    private sealed class FixtureNavigation : NavigationManager
    {
        public FixtureNavigation()
        {
            Initialize("http://localhost/", "http://localhost/project/fixture");
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    /// <summary>Guid claims exercise normal owner resolution without invoking any remote identity provider.</summary>
    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "fixture"))));
        }
    }

    /// <summary>Any unexpected user lookup fails this credential-free fixture.</summary>
    public class UnusedUserService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            throw new InvalidOperationException("Unexpected user lookup");
        }
    }

    /// <summary>Recorded channels and a v2 historical result make both provider-free page previews representative.</summary>
    public class FixtureAssessments : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var now = DateTimeOffset.UtcNow;
            return method?.Name switch
            {
                "GetPreferencesAsync" => Task.FromResult<AssessmentPreferences?>(new AssessmentPreferences(
                    new AssessmentSelection(),
                    [
                        new AssessmentChannel(AssessmentSources.Build, "build"),
                        new AssessmentChannel(AssessmentSources.Web, "web"),
                        new AssessmentChannel(AssessmentSources.Database, "db")
                    ],
                    ["web", "db"], DeploymentStatus.Stopped)),
                "GetLatestAsync" => Task.FromResult<DeploymentAnalysisView?>(new DeploymentAnalysisView(Guid.NewGuid(),
                    (Guid)args![1]!,
                    AiAnalysisStatus.Completed, AiAnalysisTrigger.Manual,
                    "The saved activity completed successfully. The application is now stopped.",
                    [], ["order:1"], null, now, now, ResultSchemaVersion: 2,
                    Sections: new AssessmentSections(["The recorded build completed."],
                        ["The selected web metrics show observed usage, not proof of health."], [],
                        ["Only selected recorded evidence was assessed."]),
                    Assessment: new AssessmentProvenance(new AssessmentSelection(), AssessmentKind.HistoricalReview,
                        DeploymentStatus.Stopped, DeploymentOutcome.Succeeded,
                        now, new AssessmentWindow(now.AddHours(-1), now, false)))),
                "ListAsync" => Task.FromResult<IReadOnlyList<DeploymentAnalysisView>>([]),
                _ => throw new NotSupportedException()
            };
        }
    }

    /// <summary>Static rendering must not try to execute JavaScript.</summary>
    private sealed class StaticJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            throw new InvalidOperationException("Unexpected interop");
        }

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            return InvokeAsync<T>(identifier, args);
        }
    }

    /// <summary>Representative sparse history, with the same resource magnitudes as the reported screenshots.</summary>
    private sealed class FixtureHistory : IDeploymentHistoryService
    {
        public Task<DeploymentTerminalHistory> ReadLogsAsync(Guid user, Guid project, Guid deployment, long cursor,
            bool backwards, int limit, CancellationToken token = default)
        {
            return Task.FromResult(new DeploymentTerminalHistory(
                [new DeploymentTerminalLog(1, project, deployment, "build", "Build completed")], false));
        }

        public Task<DeploymentMetricHistory> ReadMetricsAsync(Guid user, Guid project, Guid deployment,
            DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken token = default)
        {
            return Task.FromResult(new DeploymentMetricHistory([
                new DeploymentMetricPoint("web", TelemetryPresentation.Cpu, "cores", end.AddHours(-1), .006, 0, .012),
                new DeploymentMetricPoint("web", TelemetryPresentation.Memory, "bytes", end.AddHours(-1), 19430000,
                    14156000, 24704000),
                new DeploymentMetricPoint("web", TelemetryPresentation.MemoryLimit, "bytes", end.AddHours(-1),
                    32490000000, 32490000000,
                    32490000000)
            ], "Some intervals contain missing observations."));
        }

        public Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid user, Guid project,
            CancellationToken token = default)
        {
            return Task.FromResult(new DeploymentTelemetryPreferences(false, false, false, "local"));
        }

        public Task SetManagedConsentAsync(Guid user, Guid project, bool enabled, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        public Task SetRuntimeCollectionAsync(Guid user, Guid project, bool enabled, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Representative daily aggregate response preserves supplied sample counts and quality flags.</summary>
    private sealed class FixtureAnalytics : IProjectTelemetryAnalytics
    {
        public Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start,
            DateTimeOffset end, CancellationToken token = default)
        {
            return Task.FromResult(new ProjectTelemetryAnalytics(1, 1, 0, null, [
                new DeploymentAnalyticsRow(Guid.NewGuid(), new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero),
                    "web",
                    TelemetryPresentation.Cpu, "cores", 2, .006, 0, .012, 0, true, end),
                new DeploymentAnalyticsRow(Guid.NewGuid(), new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero),
                    "web",
                    TelemetryPresentation.Memory, "bytes", 2, 19430000, 14156000, 24704000, 0, true, end)
            ], "Some daily statistics contain missing data."));
        }
    }
}