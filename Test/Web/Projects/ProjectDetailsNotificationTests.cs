using System.Reflection;
using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Data.Apps;
using Application.Orchestration.Jobs;
using Application.Orchestration.Status;
using Bunit;
using Domain.Enums.Projects;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Test.Web.Diagnostics;
using Web.Services;
using ProjectDetails = Web.Components.Pages.Projects.ProjectDetails;

namespace Test.Web.Projects;

/// <summary>Exercises overlapping metadata reads and notification faults on a real Blazor dispatcher.</summary>
public sealed class ProjectDetailsNotificationTests
{
    /// <summary>A failed initial query stays recoverable without an unhandled renderer exception.</summary>
    [Fact]
    public async Task Failed_initial_read_can_be_retried_in_the_same_renderer()
    {
        var query = new ControlledQuery { Fail = true };
        await using var context = CreateContext(query);
        var page = Page();

        context.ComponentFactories.Add(page);
        var rendered = context.Render<FixturePage>();
        await rendered.InvokeAsync(() => Call(page, "LoadCurrentProjectAsync"));

        Assert.NotNull(Field(page, "_projectReadError"));
        Assert.False((bool)Field(page, "_isLoading")!);

        query.Fail = false;
        await rendered.InvokeAsync(() => Call(page, "LoadCurrentProjectAsync"));

        Assert.Null(Field(page, "_projectReadError"));
        Assert.NotNull(Field(page, "_app"));
        Assert.False(context.Renderer.UnhandledException.IsCompleted);
    }

    /// <summary>Notifications coalesce around one in-flight read and disposal suppresses late callbacks.</summary>
    [Fact]
    public async Task Overlapping_refreshes_are_serialized_and_late_callbacks_are_ignored()
    {
        var query = new ControlledQuery();
        await using var context = CreateContext(query);
        var page = Page();

        context.ComponentFactories.Add(page);
        var rendered = context.Render<FixturePage>();
        await rendered.InvokeAsync(() => Call(page, "LoadCurrentProjectAsync"));
        query.Pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        query.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = rendered.InvokeAsync(() => Call(page, "RefreshProjectAsync", false));

        await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => rendered.InvokeAsync(() => Call(page, "RefreshProjectAsync", false))));

        Assert.Equal(2, query.Reads);

        query.Pending.SetResult();
        await refresh;

        Assert.Equal(3, query.Reads);
        Assert.Equal(1, query.MaximumActive);

        var called = false;

        await Call(page, "DispatchNotificationAsync",
            (Func<Task>)(() => throw new InvalidOperationException("synthetic")));

        Assert.False(context.Renderer.UnhandledException.IsCompleted);

        SetField(page, "_analysisDisposed", true);
        await Call(page, "DispatchNotificationAsync", (Func<Task>)(() =>
        {
            called = true;

            return Task.CompletedTask;
        }));

        Assert.False(called);
    }

    /// <summary>Supplies component injection while retaining controlled query responses.</summary>
    /// <param name="query">Scenario-controlled metadata responses.</param>
    /// <returns>A context owned and asynchronously disposed by the test.</returns>
    private static BunitContext CreateContext(IProjectDetailsQuery query)
    {
        var context = new BunitContext();
        context.AddAuthorization();
        context.Services.AddSingleton(query);
        context.Services.AddDataProtection();
        context.Services.AddScoped<ScopedApplicationOperations>().AddScoped<ConnectedCloudDeploymentAdmission>();
        context.Services.AddSingleton(DispatchProxy
            .Create<IProjectScannerService, OperationalLoggingTests.PortProxy>());
        context.Services.AddSingleton(DispatchProxy.Create<IDockerService, OperationalLoggingTests.PortProxy>());
        context.Services.AddSingleton<IDeploymentCapabilities>(new DeploymentCapabilities(true, true));
        context.Services.AddSingleton<IDeploymentStatusNotifier>(
            new DeploymentStatusNotifier(NullLogger<DeploymentStatusNotifier>.Instance));
        context.Services.AddSingleton<IDeploymentJobQueue>(
            new DeploymentJobQueue(Options.Create(new DeploymentConcurrencyOptions())));
        return context;
    }

    /// <summary>Supplies the real component's lifecycle and Application boundaries.</summary>
    private static FixturePage Page()
    {
        var page = new FixturePage();

        SetField(page, "_currentUserId", Guid.NewGuid());
        SetField(page, "_isLoading", false);
        return page;
    }

    /// <summary>Invokes the real private workflow rather than duplicating its behavior.</summary>
    private static Task Call(ProjectDetails page, string method, params object[] arguments)
    {
        return (Task)typeof(ProjectDetails).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            page, arguments)!;
    }

    /// <summary>Reads renderer-owned state for behavior assertions.</summary>
    private static object? Field(ProjectDetails page, string name)
    {
        return typeof(ProjectDetails).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
    }

    /// <summary>Sets controlled lifecycle state.</summary>
    private static void SetField(ProjectDetails page, string name, object value)
    {
        typeof(ProjectDetails).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    }

    /// <summary>Holds one detached metadata response to model dispatcher reentrancy.</summary>
    private sealed class ControlledQuery : IProjectDetailsQuery
    {
        /// <summary>Current in-flight reads.</summary>
        private int _active;

        /// <summary>Whether the next read fails.</summary>
        public bool Fail { get; set; }

        /// <summary>Total observed reads.</summary>
        public int Reads { get; private set; }

        /// <summary>Largest simultaneous read count.</summary>
        public int MaximumActive { get; private set; }

        /// <summary>Optional response gate.</summary>
        public TaskCompletionSource? Pending { get; set; }

        /// <summary>Signals that the gate has been reached.</summary>
        public TaskCompletionSource? Entered { get; set; }

        /// <summary>Returns owned remote metadata or a controlled failure.</summary>
        public async Task<ProjectDetailsReadModel?> ReadAsync(Guid project, Guid owner, int historyPage,
            CancellationToken token = default)
        {
            Reads++;
            MaximumActive = Math.Max(MaximumActive, ++_active);
            try
            {
                if (Fail) throw new InvalidOperationException("synthetic provider detail");
                if (Pending is { } pending)
                {
                    Entered?.TrySetResult();
                    await pending.Task.WaitAsync(token);
                    Pending = null;
                }

                return new ProjectDetailsReadModel(new Domain.Entities.Projects.Application
                    {
                        Id = project,
                        UserId = owner,
                        Name = "fixture",
                        SourcePathOrUrl = "https://github.com/example/fixture",
                        SourceType = SourceType.Remote
                    },
                    new ProjectDeploymentSummary(0, 0, 0, 0, null), []);
            }
            finally
            {
                _active--;
            }
        }
    }

    /// <summary>Keeps production workflows with minimal fixture markup and initialization.</summary>
    private sealed class FixturePage : ProjectDetails
    {
        /// <summary>Avoids live authentication and subscriptions during fixture initialization.</summary>
        protected override Task OnInitializedAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>Avoids routing I/O before explicit test reads.</summary>
        protected override Task OnParametersSetAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>Avoids live SignalR initialization in the renderer fixture.</summary>
        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            return Task.CompletedTask;
        }

        /// <summary>Renders a minimal dispatcher-owned surface.</summary>
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddContent(0, "fixture");
        }
    }
}
