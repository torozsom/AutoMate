using System.Reflection;
using System.Security.Claims;
using System.Threading.Channels;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Orchestration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Exercises identity changes and disposal against actual asynchronous overview read lifetime.</summary>
#pragma warning disable BL0006 // Real renderer required for interleaved lifecycle callbacks.
public sealed class WorkspaceOverviewLifecycleTests
{
    /// <summary>Old-owner responses cannot overwrite a new account, and navigation cancels an in-flight read.</summary>
    [Fact]
    public async Task Identity_switch_fences_old_response_and_disposal_cancels_reads()
    {
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var query = new ControlledQuery();
        var authentication = new FixtureAuthentication(secondOwner);
        using var services = new ServiceCollection().AddSingleton<IWorkspaceQuery>(query)
            .AddSingleton(DispatchProxy.Create<IUserService, ConsoleRenderingTests.EmptyPort>()).BuildServiceProvider();
        var panel = new FixturePanel();
        SetField("_owner", firstOwner);
        SetProperty("Scopes", services.GetRequiredService<IServiceScopeFactory>());
        SetProperty("Authentication", authentication);
        SetProperty("Logger", NullLogger<WorkspaceOverviewPanel>.Instance);
        SetProperty("StatusNotifier", new DeploymentStatusNotifier(NullLogger<DeploymentStatusNotifier>.Instance));
        var renderer = new FixtureRenderer(services);
        try
        {
            await renderer.Dispatcher.InvokeAsync(() => renderer.AttachAsync(panel));
            var oldRead = renderer.Dispatcher.InvokeAsync(() => Call("LoadAsync"));
            var oldRequest = await query.NextAsync();
            Assert.Equal(firstOwner, oldRequest.Owner);
            var switched = renderer.Dispatcher.InvokeAsync(() => Call("RefreshNotificationAsync", true));
            var currentRequest = await query.NextAsync();
            Assert.Equal(secondOwner, currentRequest.Owner);
            currentRequest.Reply.SetResult(Data(2));
            await switched;
            Assert.True(oldRequest.Token.IsCancellationRequested);
            oldRequest.Reply.TrySetResult(Data(1));
            await oldRead;
            Assert.Equal(2, ((WorkspaceOverview)GetField("_data")!).SavedProjects);

            var pending = renderer.Dispatcher.InvokeAsync(() => Call("LoadAsync"));
            var request = await query.NextAsync();
            await renderer.DisposeAsync();
            await pending;
            Assert.True(request.Token.IsCancellationRequested);
            Assert.Equal(2, ((WorkspaceOverview)GetField("_data")!).SavedProjects);
            Assert.Empty(renderer.Errors);
        }
        finally
        {
            await renderer.DisposeAsync();
        }

        /// <summary>Seeds actual private component state without production-only test hooks.</summary>
        void SetField(string name, object value)
        {
            typeof(WorkspaceOverviewPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                panel, value);
        }

        /// <summary>Reads the component's accepted projection.</summary>
        object? GetField(string name)
        {
            return typeof(WorkspaceOverviewPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel);
        }

        /// <summary>Injects the real independent scope and authentication boundaries.</summary>
        void SetProperty(string name, object value)
        {
            typeof(WorkspaceOverviewPanel).GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(panel, value);
        }

        /// <summary>Runs real lifecycle helpers on the renderer dispatcher.</summary>
        Task Call(string name, params object?[] args)
        {
            return (Task)typeof(WorkspaceOverviewPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, args)!;
        }
    }

    /// <summary>Minimal projection whose owner-specific value exposes stale acceptance.</summary>
    private static WorkspaceOverview Data(int projects)
    {
        return new WorkspaceOverview(DateTimeOffset.UtcNow.AddDays(-29),
            DateTimeOffset.UtcNow, projects, 0, 0, 0, 0, 0, null, [], [], [], [], null);
    }

    /// <summary>Real read lifetime with minimal rendering and no browser work.</summary>
    private sealed class FixturePanel : WorkspaceOverviewPanel
    {
        /// <inheritdoc />
        protected override Task OnInitializedAsync()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddContent(0, "fixture");
        }
    }

    /// <summary>Stable replacement account identity.</summary>
    private sealed class FixtureAuthentication(Guid owner) : AuthenticationStateProvider
    {
        /// <inheritdoc />
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(
                new AuthenticationState(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.ToString())],
                        "fixture"))));
        }
    }

    /// <summary>Captures owned requests for deliberately out-of-order completion.</summary>
    private sealed class ControlledQuery : IWorkspaceQuery
    {
        /// <summary>Fixture requests arriving from independent scopes.</summary>
        private readonly Channel<Request> _requests =
            Channel.CreateUnbounded<Request>();

        /// <inheritdoc />
        public Task<WorkspaceOverview> OverviewAsync(Guid owner, int days, CancellationToken token = default)
        {
            var reply = new TaskCompletionSource<WorkspaceOverview>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => reply.TrySetCanceled(token));
            _requests.Writer.TryWrite(new Request(owner, reply, token));
            return reply.Task;
        }

        /// <inheritdoc />
        public Task<ProjectInventoryPage> ProjectsAsync(Guid owner, ProjectInventoryRequest request,
            CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>Bounds waits so a lifecycle regression fails promptly.</summary>
        public Task<Request> NextAsync()
        {
            return _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Captured request and its cancellation lifetime.</summary>
    private sealed record Request(Guid Owner, TaskCompletionSource<WorkspaceOverview> Reply, CancellationToken Token);

    /// <summary>Captures renderer failures without browser dependencies.</summary>
    private sealed class FixtureRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        /// <inheritdoc />
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Unexpected render failures.</summary>
        public List<Exception> Errors { get; } = [];

        /// <summary>Attaches a component to its actual render handle.</summary>
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
            return Task.CompletedTask;
        }
    }
#pragma warning restore BL0006
}