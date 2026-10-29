using System.Reflection;
using System.Security.Claims;
using System.Threading.Channels;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Orchestration.Status;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Test.Web.Presentation;
using Web.Components.Shared.Workspace;

namespace Test.Web.Projects;

/// <summary>
///     Exercises identity changes and disposal against actual asynchronous overview read lifetime.
/// </summary>
/// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
public sealed class WorkspaceOverviewLifecycleTests
{
    /// <summary>
    ///     Old-owner responses cannot overwrite a new account, and navigation cancels an in-flight
    ///     read.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

        // <summary>
        //     Seeds actual private component state without production-only test hooks.
        // </summary>
        // <param name="name">Fixture member, option or metric name selected by the caller.</param>
        // <param name="value">Fixture member value to assign.</param>
        void SetField(string name, object value)
        {
            typeof(WorkspaceOverviewPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                panel, value);
        }

        // <summary>
        //     Reads the component's accepted projection.
        // </summary>
        // <param name="name">Fixture member, option or metric name selected by the caller.</param>
        // <returns>The selected reflected fixture value, which may be null.</returns>
        object? GetField(string name)
        {
            return typeof(WorkspaceOverviewPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel);
        }

        // <summary>
        //     Injects the real independent scope and authentication boundaries.
        // </summary>
        // <param name="name">Fixture member, option or metric name selected by the caller.</param>
        // <param name="value">Fixture member value to assign.</param>
        void SetProperty(string name, object value)
        {
            typeof(WorkspaceOverviewPanel).GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(panel, value);
        }

        // <summary>
        //     Runs real lifecycle helpers on the renderer dispatcher.
        // </summary>
        // <param name="name">Fixture member, option or metric name selected by the caller.</param>
        // <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        // <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        Task Call(string name, params object?[] args)
        {
            return (Task)typeof(WorkspaceOverviewPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, args)!;
        }
    }

    /// <summary>
    ///     Minimal projection whose owner-specific value exposes stale acceptance.
    /// </summary>
    /// <param name="projects">Number of owned projects to seed.</param>
    /// <returns>The configured workspace overview instance or response for this scenario.</returns>
    private static WorkspaceOverview Data(int projects)
    {
        return new WorkspaceOverview(DateTimeOffset.UtcNow.AddDays(-29),
            DateTimeOffset.UtcNow, projects, 0, 0, 0, 0, 0, null, [], [], [], [], null);
    }

    /// <summary>
    ///     Real read lifetime with minimal rendering and no browser work.
    /// </summary>
    private sealed class FixturePanel : WorkspaceOverviewPanel
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
        ///     Completes immediately without executing live on after render work.
        /// </summary>
        /// <param name="firstRender">Whether this is the first interactive render; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        protected override Task OnAfterRenderAsync(bool firstRender)
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

    /// <summary>
    ///     Stable replacement account identity.
    /// </summary>
    /// <param name="owner">Synthetic owner identity.</param>
    private sealed class FixtureAuthentication(Guid owner) : AuthenticationStateProvider
    {
        /// <summary>
        ///     Supplies the synthetic principal selected by the fixture authentication state.
        /// </summary>
        /// <returns>A task returning the configured authentication state instance or response for this scenario.</returns>
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(
                new AuthenticationState(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.ToString())],
                        "fixture"))));
        }
    }

    /// <summary>
    ///     Captures owned requests for deliberately out-of-order completion.
    /// </summary>
    /// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
    private sealed class ControlledQuery : IWorkspaceQuery
    {
        /// <summary>Fixture requests arriving from independent scopes.</summary>
        private readonly Channel<Request> _requests =
            Channel.CreateUnbounded<Request>();

        /// <summary>
        ///     Enqueues a controlled overview response and links its completion to caller cancellation.
        /// </summary>
        /// <param name="owner">Synthetic owner identity.</param>
        /// <param name="days">Requested overview period in days; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the configured workspace overview instance or response for this scenario.</returns>
        public Task<WorkspaceOverview> OverviewAsync(Guid owner, int days, CancellationToken token = default)
        {
            var reply = new TaskCompletionSource<WorkspaceOverview>(TaskCreationOptions.RunContinuationsAsynchronously);

            token.Register(() => reply.TrySetCanceled(token));
            _requests.Writer.TryWrite(new Request(owner, reply, token));

            return reply.Task;
        }

        /// <summary>
        ///     Rejects projects because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="owner">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="request">Project filters and paging supplied to the inventory fixture; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        public Task<ProjectInventoryPage> ProjectsAsync(Guid owner, ProjectInventoryRequest request,
            CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///     Bounds waits so a lifecycle regression fails promptly.
        /// </summary>
        /// <returns>A task returning the configured request instance or response for this scenario.</returns>
        /// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
        public Task<Request> NextAsync()
        {
            return _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    ///     Captured request and its cancellation lifetime.
    /// </summary>
    /// <param name="Owner">Synthetic owner identity.</param>
    /// <param name="Reply">Completion source controlling the queued overview response.</param>
    /// <param name="Token">Cancellation token for the fixture operation.</param>
    private sealed record Request(Guid Owner, TaskCompletionSource<WorkspaceOverview> Reply, CancellationToken Token);

    /// <summary>
    ///     Captures renderer failures without browser dependencies.
    /// </summary>
    /// <param name="services">Fixture dependency provider.</param>
    private sealed class FixtureRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        /// <summary>Renderer dispatcher used to serialize fixture component updates.</summary>
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Unexpected render failures.</summary>
        public List<Exception> Errors { get; } = [];

        /// <summary>
        ///     Attaches a component to its actual render handle.
        /// </summary>
        /// <param name="component">Component rendered by the fixture.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public Task AttachAsync(IComponent component)
        {
            return RenderRootComponentAsync(AssignRootComponentId(component));
        }

        /// <summary>
        ///     Captures renderer failures for scenario assertions.
        /// </summary>
        /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
        protected override void HandleException(Exception exception)
        {
            Errors.Add(exception);
        }

        /// <summary>
        ///     Completes immediately without executing live update display work.
        /// </summary>
        /// <param name="renderBatch">Renderer update batch supplied by Blazor; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            return Task.CompletedTask;
        }
    }
}
