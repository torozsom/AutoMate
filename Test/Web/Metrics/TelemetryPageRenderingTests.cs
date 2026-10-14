using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Execution;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Hosting;
using Application.Ai.Context;
using Application.Data.Apps;
using Application.Data.Users;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Enums.Ai;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Test.Web.Presentation;
using Web.Components.Layout;
using Web.Components.Pages;
using Web.Components.Shared.Configuration;
using Web.Components.Shared.Metrics;
using Web.Services;
using ProjectDetails = Web.Components.Pages.Projects.ProjectDetails;

namespace Test.Web.Metrics;

/// <summary>
///     Renders both actual telemetry surfaces with explicit provider fixtures and no external
///     authentication.
/// </summary>
public sealed class TelemetryPageRenderingTests
{
    /// <summary>
    ///     Produces browser-reviewable HTML using the actual page and summary components.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Summary_and_history_render_compact_cards_and_collapsed_details()
    {
        var registrations = new ServiceCollection().AddLogging().AddAuthorizationCore()
            .AddSingleton<IJSRuntime, StaticJs>().AddSingleton<AuthenticationStateProvider, FixtureAuthentication>()
            .AddSingleton<AntiforgeryStateProvider, ConsoleRenderingTests.PreviewAntiforgery>()
            .AddSingleton<IDeploymentCapabilities>(new ConsoleRenderingTests.PreviewCapabilities(true))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(DispatchProxy.Create<IUserService, UnusedUserService>())
            .AddSingleton<IDeploymentHistoryService, FixtureHistory>()
            .AddSingleton<IProjectTelemetryAnalytics, FixtureAnalytics>()
            .AddSingleton<IDeploymentDetailsService, FixtureDetails>()
            .AddSingleton(DispatchProxy.Create<IDeploymentAnalysisService, FixtureAssessments>())
            .AddSingleton<NavigationManager, FixtureNavigation>();
        foreach (var componentType in new[] { typeof(ProjectDetails), typeof(MainLayout), typeof(NavMenu) })
            foreach (var property in componentType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                         .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null && p.PropertyType.IsInterface &&
                                     !p.PropertyType.IsGenericType))
                if (!registrations.Any(r => r.ServiceType == property.PropertyType))
                    registrations.AddSingleton(property.PropertyType,
                        DispatchProxy.Create(property.PropertyType, typeof(EmptyPort)));
        if (!registrations.Any(registration => registration.ServiceType == typeof(IUserService)))
            registrations.AddSingleton(DispatchProxy.Create<IUserService, EmptyPort>());
        if (!registrations.Any(registration => registration.ServiceType == typeof(IApplicationService)))
            registrations.AddSingleton(DispatchProxy.Create<IApplicationService, EmptyPort>());
        registrations.AddScoped<ScopedApplicationOperations>().AddScoped<ConnectedCloudDeploymentAdmission>();
        using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var project = await Render<ProjectTelemetrySummary>(renderer,
            new Dictionary<string, object?> { ["ProjectId"] = Guid.NewGuid(), ["UserId"] = Guid.NewGuid() });
        var history = await Render<DeploymentHistory>(renderer,
            new Dictionary<string, object?> { ["ProjectId"] = Guid.NewGuid(), ["DeploymentId"] = Guid.NewGuid() });
        Assert.Contains("Per observed container", Regex.Replace(project, @"\s+", " "));
        var details = await Render<FixtureProject>(renderer,
            new Dictionary<string, object?> { ["ProjectId"] = Guid.NewGuid() });
        Assert.Contains("Azure Databases", details);
        Assert.Contains("azure-db-term", details);
        Assert.Contains("aria-haspopup=\"dialog\"", details);
        Assert.DoesNotContain("id=\"ai-analysis\"", details);
        Assert.DoesNotContain("href=\"#ai-analysis\"", details);
        var analysisDialog = await Render<FixtureProject>(renderer,
            new Dictionary<string, object?> { ["ProjectId"] = Guid.NewGuid(), ["AnalysisOpen"] = true });
        Assert.Contains("role=\"dialog\"", analysisDialog);
        Assert.Contains("assessment-context", analysisDialog);
        var anchors = new[]
            { "overview", "logs", "metrics", "analytics", "configuration", "deployments" };
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
        Assert.Contains("View Details", Regex.Replace(details, @"\s+", " "));
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
        details = await Render<ConsoleRenderingTests.PreviewRoot>(renderer,
            new Dictionary<string, object?> { ["Body"] = (RenderFragment)(b => b.AddMarkupContent(0, details)) });
        history = await Render<ConsoleRenderingTests.PreviewRoot>(renderer,
            new Dictionary<string, object?> { ["Body"] = (RenderFragment)(b => b.AddMarkupContent(0, history)) });
        foreach (var page in new[]
                 {
                     (Name: "project", Html: project), (Name: "history", Html: history),
                     (Name: "details", Html: details), (Name: "details-analysis", Html: analysisDialog)
                 })
            await File.WriteAllTextAsync(Path.Combine(directory, page.Name + ".html"), Wrap(page.Html));
    }

    /// <summary>
    ///     Renders a real component through its normal parameter lifecycle.
    /// </summary>
    /// <param name="renderer">Fixture HTML renderer.</param>
    /// <param name="parameters">Component parameters supplied by the rendering fixture.</param>
    /// <typeparam name="T">Interface or result type created by the fixture.</typeparam>
    /// <returns>A task returning the synthetic text produced for this scenario.</returns>
    private static Task<string> Render<T>(HtmlRenderer renderer, Dictionary<string, object?> parameters)
        where T : IComponent
    {
        return renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    /// <summary>
    ///     Adds local theme styles and client inspection to a clearly identified provider-fixture
    ///     preview.
    /// </summary>
    /// <param name="content">Provider response or source text to read within bounds.</param>
    /// <returns>The synthetic text produced for this scenario.</returns>
    private static string Wrap(string content)
    {
        return
            "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><link rel='stylesheet' href='/bootstrap.css'><link rel='stylesheet' href='/app.css'><link rel='stylesheet' href='/Web.styles.css'><link rel='stylesheet' href='/telemetry.css'><link rel='stylesheet' href='/console.css'><link rel='stylesheet' href='/obsidian.css'><link rel='stylesheet' href='/lib/bootstrap-icons/font/bootstrap-icons.css'></head><body><div style='padding:8px 16px;font-size:11px'><small>UI verification · example provider data</small><button style='margin-left:16px' onclick=\"document.documentElement.dataset.bsTheme=document.documentElement.dataset.bsTheme==='dark'?'light':'dark'\">Toggle theme</button><button onclick=\"document.documentElement.style.fontSize='200%'\">200% text</button></div>" +
            content +
            "<script type='module'>import {attach} from '/js/telemetry-chart.js';document.querySelectorAll('.telemetry-chart').forEach(attach);import {attach as attachSections} from '/js/project-sections.js';document.querySelectorAll('.project-section-tabs').forEach(attachSections);" +
            "import {attachDialog} from '/js/console-ui.js';const shortcut=document.querySelector('button.terminal-ai-shortcut');if(shortcut)shortcut.addEventListener('click',async()=>{const html=await fetch('/details-analysis.html').then(r=>r.text());const fragment=new DOMParser().parseFromString(html,'text/html').querySelector('.console-dialog-backdrop');document.body.append(fragment);const root=fragment.querySelector('[role=dialog]');let registration;const close=()=>{fragment.remove();registration.dispose();};registration=attachDialog(root,{invokeMethodAsync(){close();return Promise.resolve();}});root.querySelector('header button').addEventListener('click',close);});</script></body></html>";
    }

    /// <summary>
    ///     Finds the repository independently of test-output configuration.
    /// </summary>
    /// <returns>The synthetic text produced for this scenario.</returns>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutoMate.slnx")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>
    ///     Renders the real project markup without authentication/provider lifecycle side effects.
    /// </summary>
    public sealed class FixtureProject : ProjectDetails
    {
        /// <summary>Renders the real analysis dialog for provider-free browser verification.</summary>
        [Parameter]
        public bool AnalysisOpen { get; set; }

        /// <summary>
        ///     Seeds project and deployment presentation state without executing the live page
        ///     initialization.
        /// </summary>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        protected override Task OnInitializedAsync()
        {
            var project = new CsProject
            { Name = "Web", IsWebProject = true, Configuration = new Configuration { DotNetVersion = "10.0" } };
            var app = new Domain.Entities.Projects.Application
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
            typeof(ProjectDetails).GetField("_showAnalysisDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, AnalysisOpen);
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Completes immediately without executing live on after render work.
        /// </summary>
        /// <param name="firstRender">Whether this is the first interactive render; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Provider-free metadata for the selected historical route.
    /// </summary>
    private sealed class FixtureDetails : IDeploymentDetailsService
    {
        /// <summary>
        ///     Supplies a stopped deployment with recorded timing for the history preview.
        /// </summary>
        /// <param name="owner">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deployment">Synthetic deployment identity.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment result, or null when the fixture supplies no value.</returns>
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

    /// <summary>
    ///     Unused injected operations return bounded empty fixture data, never perform provider work.
    /// </summary>
    public class EmptyPort : DispatchProxy
    {
        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="args">
        ///     Arguments for the selected fixture port or reflected component member; ignored by this
        ///     fixture.
        /// </param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
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

    /// <summary>
    ///     Only local URLs are needed by the static project fixture.
    /// </summary>
    private sealed class FixtureNavigation : NavigationManager
    {
        /// <summary>
        ///     Initializes the fixture navigation fixture and its required test dependencies.
        /// </summary>
        public FixtureNavigation()
        {
            Initialize("http://localhost/", "http://localhost/project/fixture");
        }

        /// <summary>
        ///     Performs no navigate to core work in this fixture.
        /// </summary>
        /// <param name="uri">Validated provider endpoint URI; ignored by this fixture.</param>
        /// <param name="forceLoad">
        ///     Whether navigation requested a full load; the preview does not navigate; ignored by this
        ///     fixture.
        /// </param>
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    /// <summary>
    ///     Guid claims exercise normal owner resolution without invoking any remote identity provider.
    /// </summary>
    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        /// <summary>
        ///     Supplies the synthetic principal selected by the fixture authentication state.
        /// </summary>
        /// <returns>A task returning the configured authentication state instance or response for this scenario.</returns>
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "fixture"))));
        }
    }

    /// <summary>
    ///     Any unexpected user lookup fails this credential-free fixture.
    /// </summary>
    public class UnusedUserService : DispatchProxy
    {
        /// <summary>
        ///     Rejects invoke because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation; ignored by this fixture.</param>
        /// <param name="args">
        ///     Arguments for the selected fixture port or reflected component member; ignored by this
        ///     fixture.
        /// </param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            throw new InvalidOperationException("Unexpected user lookup");
        }
    }

    /// <summary>
    ///     Recorded channels and a v2 historical result make both provider-free page previews
    ///     representative.
    /// </summary>
    public class FixtureAssessments : DispatchProxy
    {
        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
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

    /// <summary>
    ///     Static rendering must not try to execute JavaScript.
    /// </summary>
    private sealed class StaticJs : IJSRuntime
    {
        /// <summary>
        ///     Rejects invoke because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="identifier">Provider or user identity to resolve; ignored by this fixture.</param>
        /// <param name="args">
        ///     Arguments for the selected fixture port or reflected component member; ignored by this
        ///     fixture.
        /// </param>
        /// <typeparam name="T">Type of the requested interop result.</typeparam>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            throw new InvalidOperationException("Unexpected interop");
        }

        /// <summary>
        ///     Delegates to the fixture interop overload using the same identifier and arguments.
        /// </summary>
        /// <param name="identifier">Provider or user identity to resolve.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <typeparam name="T">Type of the requested interop result.</typeparam>
        /// <returns>A value task returning the configured fixture reference or default requested value.</returns>
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            return InvokeAsync<T>(identifier, args);
        }
    }

    /// <summary>
    ///     Representative sparse history, with the same resource magnitudes as the reported
    ///     screenshots.
    /// </summary>
    private sealed class FixtureHistory : IDeploymentHistoryService
    {
        /// <summary>
        ///     Supplies configured saved terminal history and records any fixture read count.
        /// </summary>
        /// <param name="user">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity.</param>
        /// <param name="deployment">Synthetic deployment identity.</param>
        /// <param name="cursor">Opaque history position, or null to read the latest page; ignored by this fixture.</param>
        /// <param name="backwards">
        ///     Whether to read earlier records rather than records after the cursor; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="limit">Requested count or configured admission bound; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment terminal history instance or response for this scenario.</returns>
        public Task<DeploymentTerminalHistory> ReadLogsAsync(Guid user, Guid project, Guid deployment, long cursor,
            bool backwards, int limit, CancellationToken token = default)
        {
            return Task.FromResult(new DeploymentTerminalHistory(
                [new DeploymentTerminalLog(1, project, deployment, "build", "Build completed")], false));
        }

        /// <summary>
        ///     Supplies configured saved metric history for component assertions.
        /// </summary>
        /// <param name="user">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deployment">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="start">UTC observation-window start; ignored by this fixture.</param>
        /// <param name="end">UTC observation-window end.</param>
        /// <param name="limit">Requested count or configured admission bound; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment metric history instance or response for this scenario.</returns>
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

        /// <summary>
        ///     Supplies fixed legacy telemetry preferences for the rendering fixture.
        /// </summary>
        /// <param name="user">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment telemetry preferences instance or response for this scenario.</returns>
        public Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid user, Guid project,
            CancellationToken token = default)
        {
            return Task.FromResult(new DeploymentTelemetryPreferences(false, false, false, "local"));
        }

        /// <summary>
        ///     Rejects set managed consent because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="user">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="enabled">Whether to include the option; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        public Task SetManagedConsentAsync(Guid user, Guid project, bool enabled, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///     Rejects set runtime collection because this member is deliberately unavailable in the
        ///     fixture.
        /// </summary>
        /// <param name="user">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="enabled">Whether to include the option; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        public Task SetRuntimeCollectionAsync(Guid user, Guid project, bool enabled, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    ///     Representative daily aggregate response preserves supplied sample counts and quality flags.
    /// </summary>
    private sealed class FixtureAnalytics : IProjectTelemetryAnalytics
    {
        /// <summary>
        ///     Supplies configured synthetic records, observations or completion state for the fixture
        ///     read.
        /// </summary>
        /// <param name="user">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="start">UTC observation-window start; ignored by this fixture.</param>
        /// <param name="end">UTC observation-window end.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured project telemetry analytics instance or response for this scenario.</returns>
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
