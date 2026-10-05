using System.Reflection;
using Application.Abstractions.Ai;
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

/// <summary>Checks persisted polling races, dispatcher rendering, recovery and page lifetime cancellation.</summary>
public sealed class DeploymentAnalysisPollingTests
{
    /// <summary>A slow earlier read cannot replace a newer completed result.</summary>
    [Fact]
    public async Task Newer_read_wins()
    {
        await using var fixture = new Fixture();
        var pending = new TaskCompletionSource<DeploymentAnalysisView?>();
        fixture.Port.Read = (_, _, _) => pending.Task;
        var earlier = fixture.Read();
        var completed = fixture.View(AiAnalysisStatus.Completed);
        fixture.Port.Read = (_, _, _) => Task.FromResult<DeploymentAnalysisView?>(completed);
        Assert.True(await fixture.Read());
        pending.SetResult(fixture.View(AiAnalysisStatus.Running));
        Assert.False(await earlier);
        Assert.Same(completed, fixture.Get("_latestAnalysis"));
    }

    /// <summary>Changes in owner, deployment or lifetime invalidate an outstanding result.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("deployment")]
    [InlineData("dispose")]
    public async Task Changed_context_discards_late_read(string change)
    {
        await using var fixture = new Fixture();
        var pending = new TaskCompletionSource<DeploymentAnalysisView?>();
        CancellationToken observed = default;
        fixture.Port.Read = (owner, deployment, token) =>
        {
            Assert.Equal(fixture.Owner, owner);
            Assert.Equal(fixture.Deployment.Id, deployment);
            observed = token;
            return pending.Task;
        };
        var read = fixture.Read();
        if (change == "owner")
        {
            fixture.Set("_currentUserId", Guid.NewGuid());
        }
        else if (change == "deployment")
        {
            fixture.Deployment.Id = Guid.NewGuid();
        }
        else
        {
            await fixture.Component.DisposeAsync();
            Assert.True(observed.IsCancellationRequested);
        }

        pending.SetResult(fixture.View(AiAnalysisStatus.Completed));
        Assert.False(await read);
        Assert.Null(fixture.Get("_latestAnalysis"));
    }

    /// <summary>Background refresh renders safely, retains saved state on failure and recovers without leaking errors.</summary>
    [Fact]
    public async Task Dispatcher_poll_recovers_and_skips_busy_actions()
    {
        await using var fixture = new Fixture();
        await using var renderer = new TestRenderer(fixture.Services);
        await renderer.Dispatcher.InvokeAsync(() => renderer.AttachAsync(fixture.Component));
        var saved = fixture.View(AiAnalysisStatus.Running);
        fixture.Set("_latestAnalysis", saved);
        fixture.Port.Read = (_, _, _) =>
            Task.FromException<DeploymentAnalysisView?>(new Exception("private-provider-error"));
        await Task.Run(fixture.Poll);
        Assert.Same(saved, fixture.Get("_latestAnalysis"));
        Assert.Contains("temporarily unavailable", (string)fixture.Get("_analysisMessage")!);
        Assert.DoesNotContain("private", (string)fixture.Get("_analysisMessage")!);
        var completed = fixture.View(AiAnalysisStatus.Completed);
        fixture.Port.Read = (_, _, _) => Task.FromResult<DeploymentAnalysisView?>(completed);
        await Task.Run(fixture.Poll);
        Assert.Same(completed, fixture.Get("_latestAnalysis"));
        Assert.Null(fixture.Get("_analysisMessage"));
        fixture.Set("_analysisBusy", true);
        fixture.Port.Read = (_, _, _) => throw new InvalidOperationException("Busy polling must be skipped.");
        await Task.Run(fixture.Poll);
        Assert.Empty(renderer.Errors);
    }

    /// <summary>A late polling failure cannot overwrite feedback from a newer owner action.</summary>
    [Fact]
    public async Task Late_failure_preserves_newer_feedback()
    {
        await using var fixture = new Fixture();
        await using var renderer = new TestRenderer(fixture.Services);
        await renderer.Dispatcher.InvokeAsync(() => renderer.AttachAsync(fixture.Component));
        var pending = new TaskCompletionSource<DeploymentAnalysisView?>();
        fixture.Port.Read = (_, _, _) => pending.Task;
        var poll = fixture.Poll();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            fixture.Set("_analysisReadVersion", (long)fixture.Get("_analysisReadVersion")! + 1);
            fixture.Set("_analysisMessage", "Analysis queued.");
        });
        pending.SetException(new Exception("private"));
        await poll;
        Assert.Equal("Analysis queued.", fixture.Get("_analysisMessage"));
    }

    /// <summary>Uses the actual page methods with owner metadata and isolated service scopes.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>Reflection flags for page-owned implementation details.</summary>
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>Seeds only dependencies needed for reads and disposal.</summary>
        public Fixture()
        {
            var service = DispatchProxy.Create<IDeploymentAnalysisService, ReadPort>();
            Port = (ReadPort)service;
            Services = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
            Set("_currentUserId", Owner);
            Set("_app",
                new Domain.Entities.Application
                {
                    Name = "fixture", SourceType = SourceType.Local, SourcePathOrUrl = "fixture",
                    CsProjects = [new CsProject { Deployments = [Deployment] }]
                });
            Property("ScopeFactory", Services.GetRequiredService<IServiceScopeFactory>());
            Property("DeploymentStatusNotifier",
                new DeploymentStatusNotifier(NullLogger<DeploymentStatusNotifier>.Instance));
            var queue = DispatchProxy.Create<IDeploymentJobQueue, OperationalLoggingTests.PortProxy>();
            ((OperationalLoggingTests.PortProxy)queue).Call = (_, _) => null;
            Property("DeploymentJobQueue", queue);
        }

        /// <summary>Real component with lifecycle dependencies replaced for this fixture.</summary>
        public FixturePage Component { get; } = new();

        /// <summary>Owner identity passed to every persisted read.</summary>
        public Guid Owner { get; } = Guid.NewGuid();

        /// <summary>Selected failed deployment.</summary>
        public Deployment Deployment { get; } = new() { Status = DeploymentStatus.Failed };

        /// <summary>Fake owner-authorized read boundary.</summary>
        public ReadPort Port { get; }

        /// <summary>Scope provider without a database or live provider.</summary>
        public ServiceProvider Services { get; }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return Services.DisposeAsync();
        }

        /// <summary>Returns synthetic persisted metadata.</summary>
        public DeploymentAnalysisView View(AiAnalysisStatus status)
        {
            return new DeploymentAnalysisView(Guid.NewGuid(), Deployment.Id, status, AiAnalysisTrigger.Manual, null, [],
                [], null,
                DateTimeOffset.UtcNow, null);
        }

        /// <summary>Invokes the actual versioned scoped read.</summary>
        public Task<bool> Read()
        {
            return (Task<bool>)typeof(ProjectDetails).GetMethod("RefreshLatestAnalysisAsync", Flags)!.Invoke(Component,
                [CancellationToken.None])!;
        }

        /// <summary>Invokes one actual automatic polling tick.</summary>
        public Task Poll()
        {
            return (Task)typeof(ProjectDetails).GetMethod("PollAnalysisOnceAsync", Flags)!.Invoke(Component,
                [CancellationToken.None])!;
        }

        /// <summary>Seeds page state.</summary>
        public void Set(string field, object value)
        {
            typeof(ProjectDetails).GetField(field, Flags)!.SetValue(Component, value);
        }

        /// <summary>Observes page state.</summary>
        public object? Get(string field)
        {
            return typeof(ProjectDetails).GetField(field, Flags)!.GetValue(Component);
        }

        /// <summary>Supplies a private injected dependency.</summary>
        private void Property(string property, object value)
        {
            typeof(ProjectDetails).GetProperty(property, Flags)!.SetValue(Component, value);
        }
    }

    /// <summary>Runs the real polling methods without authentication, hubs or full page rendering.</summary>
    private sealed class FixturePage : ProjectDetails
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

    /// <summary>Controls read completion and observes lifetime cancellation without external I/O.</summary>
    public class ReadPort : DispatchProxy
    {
        /// <summary>Read behavior for the current test.</summary>
        public Func<Guid, Guid, CancellationToken, Task<DeploymentAnalysisView?>> Read { get; set; } =
            (_, _, _) => Task.FromResult<DeploymentAnalysisView?>(null);

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name == "GetLatestAsync"
                ? Read((Guid)args![0]!, (Guid)args[1]!, (CancellationToken)args[2]!)
                : throw new NotSupportedException();
        }
    }

#pragma warning disable BL0006 // Real renderer verifies dispatcher-bound background updates.
    /// <summary>Captures rendering errors on the real Blazor dispatcher.</summary>
    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        /// <inheritdoc />
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Unhandled renderer errors.</summary>
        public List<Exception> Errors { get; } = [];

        /// <summary>Attaches the component to a render handle.</summary>
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