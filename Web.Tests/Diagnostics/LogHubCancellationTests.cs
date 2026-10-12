using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Application.Data.Apps;
using Application.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Web.Hubs;
using Xunit;

namespace Web.Tests;

/// <summary>
///     Distinguishes expected subscription disconnects from genuine provider cancellation.
/// </summary>
/// <exception cref="OperationCanceledException">The returned task faults when the fixture failure is selected.</exception>
public sealed class LogHubCancellationTests
{
    /// <summary>
    ///     Disconnecting during replay must not become a hub failure or confirm an empty cursor.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Disconnect_during_replay_returns_unconfirmed_empty_history()
    {
        using var disconnected = new CancellationTokenSource();
        var (hub, project, deployment, token) = CreateHub(disconnected, true);
        var history = await hub.JoinProjectGroup(project, deployment, token);
        Assert.Empty(history.Events);
        Assert.False(history.CanAdvanceCursor);
    }

    /// <summary>
    ///     A canceled provider request with an active connection must remain observable.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Provider_cancellation_without_disconnect_is_not_swallowed()
    {
        using var disconnected = new CancellationTokenSource();
        var (hub, project, deployment, token) = CreateHub(disconnected, false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => hub.JoinProjectGroup(project, deployment, token));
    }

    /// <summary>
    ///     Cancellation handling does not weaken subscription token checks.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Invalid_token_is_still_rejected()
    {
        using var disconnected = new CancellationTokenSource();
        var (hub, project, deployment, _) = CreateHub(disconnected, true);
        await Assert.ThrowsAsync<HubException>(() => hub.JoinProjectGroup(project, deployment, "invalid"));
        Assert.False(disconnected.IsCancellationRequested);
    }

    /// <summary>
    ///     Join/replay spans share one trace and separate expected disconnects from provider failure.
    /// </summary>
    /// <param name="disconnect">Whether the fixture disconnects the live connection.</param>
    /// <param name="outcome">Configured or expected operation outcome.</param>
    /// <param name="status">Expected or synthetic trace status.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(true, "canceled", ActivityStatusCode.Unset)]
    [InlineData(false, "failed", ActivityStatusCode.Error)]
    public async Task Join_and_replay_spans_report_safe_correlated_outcomes(bool disconnect, string outcome,
        ActivityStatusCode status)
    {
        using var exporter = new TraceSnapshotExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(AutoMateTelemetry.Deployments.Name)
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using var canceled = new CancellationTokenSource();
        var (hub, project, deployment, token) = CreateHub(canceled, disconnect);
        if (disconnect) await hub.JoinProjectGroup(project, deployment, token);
        else
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                hub.JoinProjectGroup(project, deployment, token));
        var spans = exporter.Spans.Where(span => Equals(span.Tags.GetValueOrDefault("deployment.project.id"), project))
            .ToArray();
        var join = Assert.Single(spans, span => span.Name == "deployment.signalr.join");
        var replay = Assert.Single(spans, span => span.Name == "deployment.signalr.replay");
        Assert.Equal(join.TraceId, replay.TraceId);
        Assert.Equal(join.SpanId, replay.ParentSpanId);
        Assert.All(spans, span =>
        {
            Assert.Equal(status, span.Status);
            Assert.Equal(outcome, span.Tags["deployment.outcome"]);
            Assert.Equal(deployment, span.Tags["deployment.id"]);
            Assert.Null(span.Description);
            Assert.Equal(0, span.EventCount);
        });
        Assert.DoesNotContain(token, JsonSerializer.Serialize(spans));
    }

    /// <summary>
    ///     Creates an authorized subscription whose storage read simulates cancellation.
    /// </summary>
    /// <param name="disconnected">Cancellation source or token representing connection loss.</param>
    /// <param name="disconnectDuringReplay">Whether to cancel while saved output is being read.</param>
    /// <returns>
    ///     The grouped synthetic identities or fixture observations. A configured outage returns a
    ///     faulted task instead.
    /// </returns>
    /// <exception cref="OperationCanceledException">The returned task faults when the fixture failure is selected.</exception>
    private static (LogHub Hub, Guid Project, Guid Deployment, string Token) CreateHub(
        CancellationTokenSource disconnected, bool disconnectDuringReplay)
    {
        var deployment = new Deployment { Id = Guid.NewGuid() };
        var app = new Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "test",
            SourceType = SourceType.Local,
            SourcePathOrUrl = "test",
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

    /// <summary>
    ///     Provides minimal interface collaborators without external services or mocking packages.
    /// </summary>
    /// <param name="invoke">Configured reflected port invocation callback.</param>
    /// <typeparam name="T">Interface or result type created by the fixture.</typeparam>
    /// <returns>The configured t instance or response for this scenario.</returns>
    private static T Stub<T>(Func<MethodInfo?, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)proxy).Handler = invoke;
        return proxy;
    }

    /// <summary>
    ///     Routes interface calls to the scenario-specific test behavior.
    /// </summary>
    public class InterfaceStub : DispatchProxy
    {
        /// <summary>Scenario-specific response for an interface method.</summary>
        public Func<MethodInfo?, object?[]?, object?> Handler { get; set; } = null!;

        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="targetMethod">Reflected port member dispatched by the fake.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod, args);
        }
    }

    /// <summary>
    ///     A connection with a controllable disconnect token.
    /// </summary>
    /// <param name="disconnected">Cancellation source or token representing connection loss.</param>
    private sealed class CallerContext(CancellationToken disconnected) : HubCallerContext
    {
        /// <summary>Fixed synthetic SignalR connection identity.</summary>
        public override string ConnectionId => "test-connection";

        /// <summary>No authenticated hub user identifier is supplied by this fixture.</summary>
        public override string? UserIdentifier => null;

        /// <summary>No authenticated principal is supplied by this synthetic hub context.</summary>
        public override ClaimsPrincipal? User => null;

        /// <summary>Mutable synthetic connection-local data.</summary>
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        /// <summary>Synthetic connection feature collection.</summary>
        public override IFeatureCollection Features { get; } = new FeatureCollection();

        /// <summary>Configured connection-loss cancellation token.</summary>
        public override CancellationToken ConnectionAborted => disconnected;

        /// <summary>
        ///     Performs no abort work in this fixture.
        /// </summary>
        public override void Abort()
        {
        }
    }
}