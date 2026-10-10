using System.Reflection;
using System.Security.Claims;
using Application.Abstractions.Hosting;
using Application.Data.Apps;
using Application.Orchestration;
using Domain.DTO;
using Domain.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Web.Components.Layout;
using Web.Components.Pages;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Renders real console surfaces with clearly identified metadata fixtures and no external services.</summary>
public sealed class ConsoleRenderingTests
{
    /// <summary>Checks real page structure, link targets and missing-data presentation; emits local review fixtures.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Console_pages_render_accessible_compact_surfaces(bool localEnabled)
    {
        var services = new ServiceCollection().AddLogging().AddAuthorizationCore()
            .AddSingleton<IJSRuntime, StaticJs>()
            .AddSingleton<AuthenticationStateProvider, PreviewAuthentication>()
            .AddSingleton<AntiforgeryStateProvider, PreviewAntiforgery>()
            .AddSingleton<NavigationManager, PreviewNavigation>()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IWorkspaceQuery, PreviewQuery>()
            .AddSingleton<IDeploymentCapabilities>(new PreviewCapabilities(localEnabled));
        foreach (var type in new[]
                 {
                     typeof(Dashboard), typeof(GitHubRepos), typeof(LocalGitRepos), typeof(MainLayout), typeof(NavMenu),
                     typeof(ConfigurationForm), typeof(RegistryForm), typeof(WorkspaceOverviewPanel)
                 })
        foreach (var property in type
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null && p.PropertyType.IsInterface &&
                                 !p.PropertyType.IsGenericType))
            if (!services.Any(s => s.ServiceType == property.PropertyType))
                services.AddSingleton(property.PropertyType,
                    DispatchProxy.Create(property.PropertyType, typeof(EmptyPort)));
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = Root();
        var directory = Path.Combine(root, ".artifacts", "console-preview");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "lib", "bootstrap", "dist", "css", "bootstrap.min.css"),
            Path.Combine(directory, "bootstrap.css"), true);
        File.Copy(Path.Combine(root, "Web", "obj", "Debug", "net10.0", "scopedcss", "bundle", "Web.styles.css"),
            Path.Combine(directory, "Web.styles.css"), true);
        foreach (var (name, type, authenticated) in new[]
                 {
                     ("overview", typeof(Home), true), ("projects", typeof(PreviewProjects), true),
                     ("azure", typeof(PreviewAzure), true), ("github", typeof(PreviewGitHub), true),
                     ("local", typeof(PreviewLocal), true),
                     ("landing", typeof(Home), false), ("login", typeof(LoginForm), false),
                     ("register", typeof(RegistryForm), false),
                     ("configuration", typeof(PreviewConfiguration), true), ("verify", typeof(PreviewVerify), false)
                 })
        {
            if (name == "local" && !localEnabled) continue;
            ((PreviewAuthentication)provider.GetRequiredService<AuthenticationStateProvider>()).Authenticated =
                authenticated;
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<PreviewRoot>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                        { [nameof(PreviewRoot.Page)] = type }))).ToHtmlString());
            if (name == "overview")
            {
                Assert.Contains("Success rate", html);
                Assert.Contains("UTC", html);
                Assert.DoesNotContain("All systems operational", html);
                Assert.Contains("Not recorded", html);
                if (!localEnabled)
                {
                    Assert.Contains("Azure deployment workspace", html);
                    Assert.DoesNotContain("Connecting to Docker", html);
                }
            }

            if (name == "projects")
            {
                Assert.Contains("responsive-inventory", html);
                Assert.Contains("Latest status", html);
                Assert.DoesNotContain("dashboard-project-card", html);
            }

            if (name == "azure")
            {
                Assert.Contains("aria-modal=\"true\"", html);
                Assert.Contains("/api/auth/azure-login", html);
                Assert.Contains("azure-tenant-id", html);
            }

            if (name == "login")
            {
                Assert.Contains("/api/auth/login", html);
                Assert.Contains("__RequestVerificationToken", html);
            }

            if (name == "configuration")
            {
                Assert.Contains("type=\"password\"", html);
                Assert.Contains("for=\"config-field-1\"", html);
            }

            await File.WriteAllTextAsync(Path.Combine(directory, name + (localEnabled ? "" : "-saas") + ".html"),
                Wrap(html));
        }

        var absent = new WorkspaceOverview(DateTimeOffset.UtcNow.AddDays(-6), DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0,
            null,
            Enumerable.Range(0, 7).Select(i => new WorkspaceActivity(DateTimeOffset.UtcNow.AddDays(-6 + i), 0, 0, 0))
                .ToArray(), [], [], [], "No recorded resource observations in this period.");
        var empty = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<WorkspaceOverviewView>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = absent }))).ToHtmlString());
        Assert.DoesNotContain("NaN", empty);
        Assert.Contains("No observations", empty);
        Assert.Contains("Your deployment activity", empty);
    }

    /// <summary>Example data covers partial observations, runtime stops and legacy missing durations.</summary>
    internal static WorkspaceOverview Data()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-29), TimeSpan.Zero);
        return new WorkspaceOverview(start, now, 12, 3, 28, 22, 4, 2, 83,
            Enumerable.Range(0, 30)
                .Select(i => new WorkspaceActivity(start.AddDays(i), i % 4, i % 11 == 0 ? 1 : 0, i == 12 ? 2 : 0))
                .ToArray(),
            Enumerable.Range(0, 30).Where(i => i != 14).SelectMany(i => new[]
            {
                new WorkspaceResource(start.AddDays(i), TelemetryPresentation.Cpu, "cores", 100, .07 + i * .004, .01,
                    .35, i == 18),
                new WorkspaceResource(start.AddDays(i), TelemetryPresentation.Memory, "bytes", 100,
                    (70 + i) * 1024 * 1024, 50 * 1024 * 1024, 160 * 1024 * 1024, i == 18)
            }).ToArray(),
            [
                new WorkspaceRun(Guid.NewGuid(), Guid.NewGuid(), "Commerce API", "Azure", now.AddMinutes(-14),
                    DeploymentOutcome.Succeeded, DeploymentStatus.Running, 74),
                new WorkspaceRun(Guid.NewGuid(), Guid.NewGuid(), "Worker service", "Docker", now.AddHours(-2),
                    DeploymentOutcome.Succeeded, DeploymentStatus.Stopped, 92),
                new WorkspaceRun(Guid.NewGuid(), Guid.NewGuid(), "Legacy project", "Docker", now.AddDays(-1),
                    DeploymentOutcome.Unknown, DeploymentStatus.Stopped, null)
            ],
            [
                new WorkspaceAttention(Guid.NewGuid(), "Analytics service", "Starting", Guid.NewGuid()),
                new WorkspaceAttention(Guid.NewGuid(), "Background worker", "Queued", null)
            ],
            "Some recorded daily statistics are incomplete.");
    }

    /// <summary>Compact inventory fixture.</summary>
    private static ProjectInventoryPage Inventory()
    {
        return new ProjectInventoryPage([
            new ProjectInventoryRow(Guid.NewGuid(), "Commerce API", "https://github.com/example/commerce",
                SourceType.Remote, 3, 1,
                DateTimeOffset.UtcNow.AddDays(-45), DateTimeOffset.UtcNow.AddHours(-1), DeploymentStatus.Running),
            new ProjectInventoryRow(Guid.NewGuid(), "Worker service", "C:/Projects/worker", SourceType.Local, 2, 1,
                DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddDays(-1), DeploymentStatus.Stopped),
            new ProjectInventoryRow(Guid.NewGuid(), "Analytics service", "https://github.com/example/analytics",
                SourceType.Remote, 1, 1,
                DateTimeOffset.UtcNow.AddDays(-2), null, null)
        ], 3, 1, 3, 1, 2, 1);
    }

    /// <summary>Injects private fixture state without adding production preview paths.</summary>
    private static void Set(Type type, object target, string field, object value)
    {
        type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    /// <summary>Locates the solution independently of output folder.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutoMate.slnx")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>Loads the actual styles and provider-free local browser interaction modules.</summary>
    private static string Wrap(string html)
    {
        return
            "<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><link rel='stylesheet' href='/bootstrap.css'><link rel='stylesheet' href='/app.css'><link rel='stylesheet' href='/Web.styles.css'><link rel='stylesheet' href='/telemetry.css'><link rel='stylesheet' href='/console.css'></head><body>" +
            html +
            "<script type='module'>import {attach} from '/js/telemetry-chart.js'; document.querySelectorAll('.telemetry-chart').forEach(attach); import {attachDialog} from '/js/console-ui.js'; document.querySelectorAll('[role=dialog]').forEach(root=>attachDialog(root,{invokeMethodAsync(){root.parentElement.remove(); return Promise.resolve();}})); const menu=document.querySelector('.mobile-nav-toggle');if(menu)menu.addEventListener('click',()=>{const target=document.getElementById('workspace-navigation');target.classList.toggle('nav-open');menu.setAttribute('aria-expanded',target.classList.contains('nav-open'));});</script></body></html>";
    }

    /// <summary>Runs real shell composition for both supported hosting profiles.</summary>
    private sealed class PreviewCapabilities(bool localEnabled) : IDeploymentCapabilities
    {
        /// <inheritdoc />
        public bool LocalDeploymentsEnabled => localEnabled;

        /// <inheritdoc />
        public bool CloudDeploymentsEnabled => true;
    }

    /// <summary>Owner and identity are fake and never point to live resources.</summary>
    private sealed class PreviewAuthentication : AuthenticationStateProvider
    {
        /// <summary>Selected hosting preview identity.</summary>
        public bool Authenticated { get; set; } = true;

        /// <inheritdoc />
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(
                new AuthenticationState(
                    new ClaimsPrincipal(Authenticated
                        ? new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                            new Claim(ClaimTypes.Name, "Example workspace")
                        ], "fixture")
                        : new ClaimsIdentity())));
        }
    }

    /// <summary>Provider-free fixture read model.</summary>
    private sealed class PreviewQuery : IWorkspaceQuery
    {
        /// <inheritdoc />
        public Task<WorkspaceOverview> OverviewAsync(Guid owner, int days, CancellationToken token = default)
        {
            return Task.FromResult(Data());
        }

        /// <inheritdoc />
        public Task<ProjectInventoryPage> ProjectsAsync(Guid owner, ProjectInventoryRequest request,
            CancellationToken token = default)
        {
            return Task.FromResult(Inventory());
        }
    }

    /// <summary>Cascades authentication and renders the real layout and page.</summary>
    public sealed class PreviewRoot : ComponentBase
    {
        /// <summary>Page under review.</summary>
        [Parameter]
        public Type Page { get; set; } = typeof(Home);

        /// <summary>Fixture identity.</summary>
        [Inject]
        public AuthenticationStateProvider Authentication { get; set; } = null!;

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", Authentication.GetAuthenticationStateAsync());
            b.AddAttribute(2, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<MainLayout>(0);
                child.AddAttribute(1, "Body", (RenderFragment)(page =>
                {
                    page.OpenComponent(0, Page);
                    page.CloseComponent();
                }));
                child.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    /// <summary>Real Projects markup with isolated presentation state.</summary>
    public class PreviewProjects : Dashboard
    {
        /// <inheritdoc />
        protected override Task OnInitializedAsync()
        {
            Set(typeof(Dashboard), this, "_inventory", Inventory());
            Set(typeof(Dashboard), this, "_isLoading", false);
            Set(typeof(Dashboard), this, "_isAzureConnected", true);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override Task OnParametersSetAsync()
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Real connection dialog presentation.</summary>
    public sealed class PreviewAzure : PreviewProjects
    {
        /// <inheritdoc />
        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            Set(typeof(Dashboard), this, "_showAzureConnect", true);
        }
    }

    /// <summary>Real source list presentation.</summary>
    public sealed class PreviewGitHub : GitHubRepos
    {
        /// <inheritdoc />
        protected override Task OnInitializedAsync()
        {
            Set(typeof(GitHubRepos), this, "_isLoading", false);
            Set(typeof(GitHubRepos), this, "_isGitHubUser", true);
            Set(typeof(GitHubRepos), this, "_githubRepos",
                new List<GitHubRepositoryDto>
                {
                    new()
                    {
                        Name = "commerce-api", FullName = "example/commerce-api",
                        HtmlUrl = "https://github.com/example/commerce-api", Language = "C#",
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    new()
                    {
                        Name = "frontend", FullName = "example/frontend", Language = "TypeScript",
                        HtmlUrl = "https://github.com/example/frontend", UpdatedAt = DateTimeOffset.UtcNow
                    }
                });
            return Task.CompletedTask;
        }
    }

    /// <summary>Real local source list presentation.</summary>
    public sealed class PreviewLocal : LocalGitRepos
    {
        /// <inheritdoc />
        protected override Task OnInitializedAsync()
        {
            Set(typeof(LocalGitRepos), this, "_hasScanned", true);
            Set(typeof(LocalGitRepos), this, "_localProjects",
                new List<LocalProjectDto>
                {
                    new()
                    {
                        Name = "Commerce", Path = "C:/Projects/Commerce",
                        CsProjects =
                        [
                            new CsProjectDto
                                { Name = "Web", Path = "C:/Projects/Commerce/Web.csproj", IsWebProject = true },
                            new CsProjectDto { Name = "Domain", Path = "C:/Projects/Commerce/Domain.csproj" }
                        ]
                    }
                });
            return Task.CompletedTask;
        }
    }

    /// <summary>Real configuration form, seeded without reading repository files.</summary>
    public sealed class PreviewConfiguration : ConfigurationForm
    {
        /// <inheritdoc />
        protected override void OnInitialized()
        {
            Config = new DeploymentConfigDto
            {
                ProjectName = "Commerce API", IsCloudDeployment = true, CloudAzureRegion = "westeurope",
                CloudResourceGroupName = "rg-commerce", CloudContainerAppName = "commerce-api",
                CloudRegistryName = "commerce.azurecr.io",
                Databases =
                [
                    new DatabaseConfigDto
                    {
                        DbType = "PostgreSQL", DbName = "commerce", DbUser = "example", DbPassword = "example-only"
                    }
                ]
            };
            IsCloudDeployment = true;
        }
    }

    /// <summary>Real email confirmation guidance without token operations.</summary>
    public sealed class PreviewVerify : VerifyEmail
    {
        /// <inheritdoc />
        protected override Task OnInitializedAsync()
        {
            CheckEmail = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>No external port operation is allowed in static previews.</summary>
    public class EmptyPort : DispatchProxy
    {
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.ReturnType == typeof(bool)) return true;
            if (method?.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method?.Name == "GetProjectState") return new DeploymentQueueState();
            if (method?.ReturnType.IsGenericType == true &&
                method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null,
                    [
                        method.ReturnType.GetGenericArguments()[0].IsValueType
                            ? Activator.CreateInstance(method.ReturnType.GetGenericArguments()[0])
                            : null
                    ]);
            return null;
        }
    }

    /// <summary>Preview navigation does not cause requests.</summary>
    private sealed class PreviewNavigation : NavigationManager
    {
        /// <summary>Initializes loopback fixture URLs.</summary>
        public PreviewNavigation()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        /// <inheritdoc />
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    /// <summary>Static rendering never calls browser/provider JavaScript.</summary>
    private sealed class StaticJs : IJSRuntime
    {
        /// <inheritdoc />
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            return ValueTask.FromResult(default(T)!);
        }

        /// <inheritdoc />
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            return ValueTask.FromResult(default(T)!);
        }
    }

    /// <summary>Fake form token used only by offline previews.</summary>
    private sealed class PreviewAntiforgery : AntiforgeryStateProvider
    {
        /// <inheritdoc />
        public override AntiforgeryRequestToken GetAntiforgeryToken()
        {
            return new AntiforgeryRequestToken("preview-only", "__RequestVerificationToken");
        }
    }
}