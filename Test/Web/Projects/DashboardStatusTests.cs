using System.Collections.Concurrent;
using System.Reflection;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Data.Apps;
using Application.Orchestration.Jobs;
using Application.Orchestration.Status;
using Bunit;
using Domain.DTO.Deployment;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Test.Web.Diagnostics;
using Web.Services;
using Dashboard = Web.Components.Pages.Workspace.Dashboard;

namespace Test.Web.Projects;

/// <summary>
///     Exercises background deployment notifications against a real renderer dispatcher.
/// </summary>
public sealed class DashboardStatusTests
{
    /// <summary>Deployment admission navigates within the current Blazor circuit without a popup or document reload.</summary>
    [Fact]
    public async Task Queued_deployment_navigates_within_the_current_app()
    {
        var project = Guid.NewGuid();
        var navigation = new RecordingNavigation();
        var queue = DispatchProxy.Create<IDeploymentJobQueue, OperationalLoggingTests.PortProxy>();
        LocalDeploymentJob? submitted = null;

        Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(queue).Call = (method, args) =>
        {
            Assert.Equal(nameof(IDeploymentJobQueue.EnqueueAsync), method!.Name);

            submitted = Assert.IsType<LocalDeploymentJob>(args![0]);

            return ValueTask.CompletedTask;
        };
        await using var context = CreateContext();
        var component = new FixtureDashboard();

        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton(queue);
        context.ComponentFactories.Add(component);
        var rendered = context.Render<FixtureDashboard>();
        await rendered.InvokeAsync(async () =>
            await (Task)typeof(Dashboard).GetMethod("ExecuteDeploymentAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(component, [new DeploymentConfigDto { ProjectId = project, ProjectName = "fixture" }])!);

        Assert.NotNull(submitted);
        Assert.Equal($"/project/{project}", navigation.Destination);
        Assert.False(navigation.ForceLoad);
        Assert.False(context.Renderer.UnhandledException.IsCompleted);
    }

    /// <summary>
    ///     Only an owned running local deployment with enabled hosting can enter the stop queue.
    /// </summary>
    /// <param name="source">Local or remote project source under test.</param>
    /// <param name="status">Deployment state under test.</param>
    /// <param name="localEnabled">Whether fixture capabilities enable local deployment.</param>
    /// <param name="owned">Whether the fixture resource belongs to the selected principal.</param>
    /// <param name="accepted">Expected admission or operation success.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(SourceType.Local, DeploymentStatus.Running, true, true, true)]
    [InlineData(SourceType.Local, DeploymentStatus.Stopped, true, true, false)]
    [InlineData(SourceType.Local, DeploymentStatus.Failed, true, true, false)]
    [InlineData(SourceType.Remote, DeploymentStatus.Running, true, true, false)]
    [InlineData(SourceType.Local, DeploymentStatus.Running, false, true, false)]
    [InlineData(SourceType.Local, DeploymentStatus.Running, true, false, false)]
    public async Task Inventory_stop_enforces_ownership_status_and_hosting(SourceType source,
        DeploymentStatus status, bool localEnabled, bool owned, bool accepted)
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var app = new Domain.Entities.Projects.Application
        {
            Id = id,
            UserId = owned ? owner : Guid.NewGuid(),
            Name = "fixture",
            SourceType = source,
            SourcePathOrUrl = "fixture",
            CsProjects =
            [
                new CsProject
                {
                    IsWebProject = true, Path = "C:/fixture/app.csproj",
                    Deployments = [new Deployment { Status = status }]
                }
            ]
        };
        var query = DispatchProxy.Create<IApplicationService, OperationalLoggingTests.PortProxy>();

        Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(query).Call = (method, args) =>
        {
            Assert.Equal(nameof(IApplicationService.GetAppByIdAsync), method!.Name);
            Assert.Equal(id, args![0]);
            Assert.Equal(owner, args[1]);

            return Task.FromResult<Domain.Entities.Projects.Application?>(app);
        };
        var capabilities = DispatchProxy.Create<IDeploymentCapabilities, OperationalLoggingTests.PortProxy>();

        Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(capabilities).Call = (_, _) => localEnabled;
        var queue = DispatchProxy.Create<IDeploymentJobQueue, OperationalLoggingTests.PortProxy>();
        StopLocalDeploymentJob? submitted = null;

        Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(queue).Call = (method, args) =>
        {
            if (method!.Name == nameof(IDeploymentJobQueue.GetProjectState))
                return new DeploymentQueueState(0, 0, 0, 0);
            if (method.Name == nameof(IDeploymentJobQueue.EnqueueAsync))
            {
                submitted = Assert.IsType<StopLocalDeploymentJob>(args![0]);

                return ValueTask.CompletedTask;
            }

            return null;
        };
        using var services = new ServiceCollection().AddSingleton(query).BuildServiceProvider();
        var component = new FixtureDashboard();

        typeof(Dashboard).GetField("_currentUserId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
            component, owner);
        Property("ServiceScopes", services.GetRequiredService<IServiceScopeFactory>());
        Property("DeploymentCapabilities", capabilities);
        Property("DeploymentJobQueue", queue);
        Property("Logger", NullLogger<Dashboard>.Instance);
        await (Task)typeof(Dashboard).GetMethod("StopInventoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(component, [id])!;

        Assert.Equal(accepted, submitted is not null);

        if (submitted is not null)
        {
            Assert.Equal(id, submitted.ProjectId);
            Assert.Equal(app.Name, submitted.ProjectName);
            Assert.Equal(app.CsProjects.Single().Path, submitted.CsProjectPath);
        }

        // <summary>
        //     Supplies provider-free Application ports.
        // </summary>
        // <param name="name">Fixture member, option or metric name selected by the caller.</param>
        // <param name="value">Fixture member value to assign.</param>
        void Property(string name, object value)
        {
            typeof(Dashboard).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component,
                value);
        }
    }

    /// <summary>
    ///     Terminal notifications update both status and pending controls without cross-thread
    ///     rendering errors.
    /// </summary>
    /// <param name="status">Deployment state under test.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Stopped)]
    public async Task Background_terminal_status_updates_render_on_dispatcher(DeploymentStatus status)
    {
        await using var context = CreateContext();
        var component = new FixtureDashboard();
        var deployment = new Deployment { Status = DeploymentStatus.Starting };
        var app = new Domain.Entities.Projects.Application
        {
            Id = Guid.NewGuid(),
            Name = "fixture",
            SourceType = SourceType.Local,
            SourcePathOrUrl = "fixture",
            CsProjects = [new CsProject { Deployments = [deployment] }]
        };

        Set("_apps", new List<Domain.Entities.Projects.Application> { app });
        var states =
            (ConcurrentDictionary<Guid, bool>)typeof(Dashboard).GetField("_deployingStates",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;

        states[app.Id] = true;
        var queue = DispatchProxy.Create<IDeploymentJobQueue, OperationalLoggingTests.PortProxy>();

        Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(queue).Call = (_, _) => null;
        context.Services.AddSingleton(queue);
        context.ComponentFactories.Add(component);
        var rendered = context.Render<FixtureDashboard>();
        var handler =
            typeof(Dashboard).GetMethod("DispatchStatusChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Task.Run(async () => await (Task)handler.Invoke(component, [app.Id, status])!);

        Assert.Equal(status, deployment.Status);
        Assert.False(states.ContainsKey(app.Id));
        Assert.True(rendered.RenderCount >= 2);
        Assert.False(context.Renderer.UnhandledException.IsCompleted);

        component.Dispose();
        await Task.Run(async () => await (Task)handler.Invoke(component, [app.Id, DeploymentStatus.Starting])!);

        Assert.Equal(status, deployment.Status);

        // <summary>
        //     Seeds presentation state without database or authentication work.
        // </summary>
        // <param name="field">Private field name accessed by the fixture.</param>
        // <param name="value">Fixture member value to assign.</param>
        void Set(string field, object value)
        {
            typeof(Dashboard).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component,
                value);
        }
    }

    /// <summary>
    ///     Notifications refresh the bounded inventory even after deployment preparation cached an
    ///     entity.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Selected_project_status_refreshes_inventory_projection()
    {
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = new Deployment { Status = DeploymentStatus.Starting };
        var app = new Domain.Entities.Projects.Application
        {
            Id = project,
            Name = "fixture",
            SourceType = SourceType.Local,
            SourcePathOrUrl = "fixture",
            CsProjects = [new CsProject { Deployments = [deployment] }]
        };
        var initial = new ProjectInventoryPage(
            [
                new ProjectInventoryRow(project, "fixture", "fixture", SourceType.Local, 1, 1, DateTimeOffset.UtcNow,
                    null,
                    DeploymentStatus.Starting)
            ],
            1, 1, 1, 0, 0, 1);
        var updated = initial with { Items = [initial.Items[0] with { Status = DeploymentStatus.Failed }] };
        var query = DispatchProxy.Create<IWorkspaceQuery, OperationalLoggingTests.PortProxy>();
        var reads = 0;

        Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(query).Call = (method, args) =>
        {
            Assert.Equal(nameof(IWorkspaceQuery.ProjectsAsync), method!.Name);
            Assert.Equal(owner, args![0]);

            reads++;

            return Task.FromResult(updated);
        };
        await using var context = CreateContext(query);
        var component = new FixtureDashboard();

        Set("_apps", new List<Domain.Entities.Projects.Application> { app });
        Set("_inventory", initial);
        Set("_currentUserId", owner);
        var queue = DispatchProxy.Create<IDeploymentJobQueue, OperationalLoggingTests.PortProxy>();

        Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(queue).Call = (_, _) => null;
        context.Services.AddSingleton(queue);
        context.ComponentFactories.Add(component);
        context.Render<FixtureDashboard>();
        var initialReads = reads;
        var handler =
            typeof(Dashboard).GetMethod("DispatchStatusChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Task.Run(async () => await (Task)handler.Invoke(component, [project, DeploymentStatus.Failed])!);

        Assert.Equal(initialReads + 1, reads);
        Assert.Equal(DeploymentStatus.Failed,
            ((ProjectInventoryPage)typeof(Dashboard).GetField("_inventory",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!).Items[0].Status);
        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.False(context.Renderer.UnhandledException.IsCompleted);

        // <summary>
        //     Seeds actual notification state.
        // </summary>
        // <param name="name">Fixture member, option or metric name selected by the caller.</param>
        // <param name="value">Fixture member value to assign.</param>
        void Set(string name, object value)
        {
            typeof(Dashboard).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(component, value);
        }
    }

    /// <summary>Registers unused page ports and preserves the scenario's operation-scoped queries.</summary>
    /// <param name="query">Optional controlled inventory query used by status-refresh scenarios.</param>
    /// <returns>A context owned and asynchronously disposed by the test.</returns>
    private static BunitContext CreateContext(IWorkspaceQuery? query = null)
    {
        var context = new BunitContext();
        context.AddAuthorization();
        if (query is not null) context.Services.AddSingleton(query);
        context.Services.AddScoped<ScopedApplicationOperations>().AddScoped<ConnectedCloudDeploymentAdmission>();
        context.Services.AddSingleton(DispatchProxy
            .Create<IProjectScannerService, OperationalLoggingTests.PortProxy>());
        context.Services.AddSingleton<IDeploymentCapabilities>(new DeploymentCapabilities(true, true));
        context.Services.AddSingleton<IDeploymentStatusNotifier>(
            new DeploymentStatusNotifier(NullLogger<DeploymentStatusNotifier>.Instance));
        return context;
    }

    /// <summary>Records actual NavigationManager requests without opening a browser document.</summary>
    private sealed class RecordingNavigation : NavigationManager
    {
        /// <summary>Initializes the same origin used by local IDE development.</summary>
        public RecordingNavigation()
        {
            Initialize("https://localhost:7288/", "https://localhost:7288/dashboard");
        }

        /// <summary>Selected route within the existing application.</summary>
        public string? Destination { get; private set; }

        /// <summary>Whether a document reload was requested.</summary>
        public bool ForceLoad { get; private set; }

        /// <inheritdoc />
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Destination = uri;
            ForceLoad = forceLoad;
        }
    }

    /// <summary>
    ///     Retains the real status implementation with minimal rendering and no external services.
    /// </summary>
    private sealed class FixtureDashboard : Dashboard
    {
        /// <summary>
        ///     Completes immediately without executing live on initialized work.
        /// </summary>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        protected override Task OnInitializedAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Builds the synthetic component tree used by the rendering fixture.
        /// </summary>
        /// <param name="builder">Blazor builder receiving the fixture component tree.</param>
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddContent(0, "fixture");
        }
    }
}
