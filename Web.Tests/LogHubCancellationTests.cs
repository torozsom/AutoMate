using System.Reflection;
using System.Security.Claims;
using Application.Abstractions.Diagnostics;
using Application.Data.Apps;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Hubs;
using Xunit;

namespace Web.Tests;

/// <summary>Distinguishes expected subscription disconnects from genuine provider cancellation.</summary>
public sealed class LogHubCancellationTests
{
    /// <summary>Disconnecting during replay must not become a hub failure or confirm an empty cursor.</summary>
    [Fact]
    public async Task Disconnect_during_replay_returns_unconfirmed_empty_history()
    {
        using var disconnected = new CancellationTokenSource();
        var (hub, project, deployment, token) = CreateHub(disconnected, true);
        var history = await hub.JoinProjectGroup(project, deployment, token);
        Assert.Empty(history.Events);
        Assert.False(history.CanAdvanceCursor);
    }

    /// <summary>A canceled provider request with an active connection must remain observable.</summary>
    [Fact]
    public async Task Provider_cancellation_without_disconnect_is_not_swallowed()
    {
        using var disconnected = new CancellationTokenSource();
        var (hub, project, deployment, token) = CreateHub(disconnected, false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => hub.JoinProjectGroup(project, deployment, token));
    }

    /// <summary>Cancellation handling does not weaken subscription token checks.</summary>
    [Fact]
    public async Task Invalid_token_is_still_rejected()
    {
        using var disconnected = new CancellationTokenSource();
        var (hub, project, deployment, _) = CreateHub(disconnected, true);
        await Assert.ThrowsAsync<HubException>(() => hub.JoinProjectGroup(project, deployment, "invalid"));
        Assert.False(disconnected.IsCancellationRequested);
    }

    /// <summary>Creates an authorized subscription whose storage read simulates cancellation.</summary>
    private static (LogHub Hub, Guid Project, Guid Deployment, string Token) CreateHub(
        CancellationTokenSource disconnected, bool disconnectDuringReplay)
    {
        var deployment = new Deployment { Id = Guid.NewGuid() };
        var app = new Domain.Entities.Application
        {
            Id = Guid.NewGuid(), Name = "test", SourceType = SourceType.Local, SourcePathOrUrl = "test",
            CsProjects = [new CsProject { Deployments = [deployment] }]
        };
        var applications = Stub<IApplicationService>((_, _) => Task.FromResult<Domain.Entities.Application?>(app));
        var diagnostics = Stub<IDeploymentDiagnosticStore>((_, _) =>
        {
            if (disconnectDuringReplay) disconnected.Cancel();
            return Task.FromException<DeploymentTerminalHistory>(new OperationCanceledException());
        });
        var viewers = Stub<IDeploymentRuntimeViewers>((_, _) => null);
        var protection = new EphemeralDataProtectionProvider();
        var token = protection.CreateProtector("LogHub").ToTimeLimitedDataProtector()
            .Protect($"{app.Id}:{Guid.NewGuid()}", TimeSpan.FromMinutes(5));
        var hub = new LogHub(applications, diagnostics, protection, NullLogger<LogHub>.Instance, viewers)
        {
            Context = new CallerContext(disconnected.Token),
            Groups = Stub<IGroupManager>((_, _) => Task.CompletedTask)
        };
        return (hub, app.Id, deployment.Id, token);
    }

    /// <summary>Provides minimal interface collaborators without external services or mocking packages.</summary>
    private static T Stub<T>(Func<MethodInfo?, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)proxy).Handler = invoke;
        return proxy;
    }

    /// <summary>Routes interface calls to the scenario-specific test behavior.</summary>
    public class InterfaceStub : DispatchProxy
    {
        /// <summary>Scenario-specific response for an interface method.</summary>
        public Func<MethodInfo?, object?[]?, object?> Handler { get; set; } = null!;

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod, args);
    }

    /// <summary>A connection with a controllable disconnect token.</summary>
    private sealed class CallerContext(CancellationToken disconnected) : HubCallerContext
    {
        /// <inheritdoc />
        public override string ConnectionId => "test-connection";
        /// <inheritdoc />
        public override string? UserIdentifier => null;
        /// <inheritdoc />
        public override ClaimsPrincipal? User => null;
        /// <inheritdoc />
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        /// <inheritdoc />
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        /// <inheritdoc />
        public override CancellationToken ConnectionAborted => disconnected;
        /// <inheritdoc />
        public override void Abort() { }
    }
}
