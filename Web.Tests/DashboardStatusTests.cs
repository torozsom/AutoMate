using System.Collections.Concurrent;
using System.Reflection;
using Application.Data.Apps;
using Application.Orchestration;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Components.Pages;
using Xunit;

namespace Web.Tests;

/// <summary>Exercises background deployment notifications against a real renderer dispatcher.</summary>
public sealed class DashboardStatusTests
{
    /// <summary>Terminal notifications update both status and pending controls without cross-thread rendering errors.</summary>
    [Theory]
    [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Stopped)]
    public async Task Background_terminal_status_updates_render_on_dispatcher(DeploymentStatus status)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new TestRenderer(services);
        var component = new FixtureDashboard();
        var deployment = new Deployment { Status = DeploymentStatus.Starting };
        var app = new Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "fixture",
            SourceType = SourceType.Local,
            SourcePathOrUrl = "fixture",
            CsProjects = [new CsProject { Deployments = [deployment] }]
        };
        Set("_apps", new List<Domain.Entities.Application> { app });
        var states =
            (ConcurrentDictionary<Guid, bool>)typeof(Dashboard).GetField("_deployingStates",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
        states[app.Id] = true;
        typeof(Dashboard).GetProperty("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component,
            NullLogger<Dashboard>.Instance);
        typeof(Dashboard).GetProperty("DeploymentStatusNotifier", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, new DeploymentStatusNotifier(NullLogger<DeploymentStatusNotifier>.Instance));
        var queue = DispatchProxy.Create<IDeploymentJobQueue, OperationalLoggingTests.PortProxy>();
        ((OperationalLoggingTests.PortProxy)queue).Call = (_, _) => null;
        typeof(Dashboard).GetProperty("DeploymentJobQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
            component, queue);
        await renderer.Dispatcher.InvokeAsync(() => renderer.AttachAsync(component));
        var handler =
            typeof(Dashboard).GetMethod("DispatchStatusChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Task.Run(async () => await (Task)handler.Invoke(component, [app.Id, status])!);
        Assert.Equal(status, deployment.Status);
        Assert.False(states.ContainsKey(app.Id));
        Assert.True(renderer.RenderCount >= 2);
        Assert.Empty(renderer.Errors);
        component.Dispose();
        await Task.Run(async () => await (Task)handler.Invoke(component, [app.Id, DeploymentStatus.Starting])!);
        Assert.Equal(status, deployment.Status);

        /// <summary>Seeds presentation state without database or authentication work.</summary>
        void Set(string field, object value)
        {
            typeof(Dashboard).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component,
                value);
        }
    }

    /// <summary>Notifications refresh the bounded inventory even after deployment preparation cached an entity.</summary>
    [Fact]
    public async Task Selected_project_status_refreshes_inventory_projection()
    {
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = new Deployment { Status = DeploymentStatus.Starting };
        var app = new Domain.Entities.Application
        {
            Id = project, Name = "fixture", SourceType = SourceType.Local, SourcePathOrUrl = "fixture",
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
        ((OperationalLoggingTests.PortProxy)query).Call = (method, args) =>
        {
            Assert.Equal(nameof(IWorkspaceQuery.ProjectsAsync), method!.Name);
            Assert.Equal(owner, args![0]);
            reads++;
            return Task.FromResult(updated);
        };
        using var services = new ServiceCollection().AddSingleton(query).BuildServiceProvider();
        await using var renderer = new TestRenderer(services);
        var component = new FixtureDashboard();
        Set("_apps", new List<Domain.Entities.Application> { app });
        Set("_inventory", initial);
        Set("_currentUserId", owner);
        Property("ServiceScopes", services.GetRequiredService<IServiceScopeFactory>());
        Property("Logger", NullLogger<Dashboard>.Instance);
        Property("DeploymentStatusNotifier",
            new DeploymentStatusNotifier(NullLogger<DeploymentStatusNotifier>.Instance));
        var queue = DispatchProxy.Create<IDeploymentJobQueue, OperationalLoggingTests.PortProxy>();
        ((OperationalLoggingTests.PortProxy)queue).Call = (_, _) => null;
        Property("DeploymentJobQueue", queue);
        await renderer.Dispatcher.InvokeAsync(() => renderer.AttachAsync(component));
        reads = 0;
        var handler =
            typeof(Dashboard).GetMethod("DispatchStatusChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Task.Run(async () => await (Task)handler.Invoke(component, [project, DeploymentStatus.Failed])!);
        Assert.Equal(1, reads);
        Assert.Equal(DeploymentStatus.Failed,
            ((ProjectInventoryPage)typeof(Dashboard).GetField("_inventory",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!).Items[0].Status);
        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.Empty(renderer.Errors);

        /// <summary>Seeds actual notification state.</summary>
        void Set(string name, object value)
        {
            typeof(Dashboard).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(component, value);
        }

        /// <summary>Supplies provider-free Application ports.</summary>
        void Property(string name, object value)
        {
            typeof(Dashboard).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component,
                value);
        }
    }

    /// <summary>Retains the real status implementation with minimal rendering and no external services.</summary>
    private sealed class FixtureDashboard : Dashboard
    {
        /// <inheritdoc />
        protected override Task OnInitializedAsync()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddContent(0, "fixture");
        }
    }

#pragma warning disable BL0006 // A real test renderer is required to verify dispatcher behavior.
    /// <summary>Captures actual render commits and renderer exceptions.</summary>
    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        /// <inheritdoc />
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Successful renders observed on the dispatcher.</summary>
        public int RenderCount { get; private set; }

        /// <summary>Unhandled renderer errors.</summary>
        public List<Exception> Errors { get; } = [];

        /// <summary>Attaches the real component to the test renderer.</summary>
        public Task AttachAsync(IComponent component)
        {
            return RenderRootComponentAsync(AssignRootComponentId(component));
        }

        /// <inheritdoc />
        protected override void HandleException(Exception exception)
        {
            Errors.Add(exception);
        }

        /// <inheritdoc />
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            RenderCount++;
            return Task.CompletedTask;
        }
    }
#pragma warning restore BL0006
}