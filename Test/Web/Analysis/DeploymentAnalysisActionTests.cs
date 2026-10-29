using System.Reflection;
using Application.Abstractions.Ai.Execution;
using Application.Data.Apps;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Enums.Ai;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Test.Web.Diagnostics;
using Web.Components.Shared;

namespace Test.Web.Analysis;

/// <summary>
///     Exercises real owner consent/cancel handlers with controlled Application ports.
/// </summary>
public sealed class DeploymentAnalysisActionTests
{
    /// <summary>
    ///     Consent uses the selected deployment's exact project and updates no sibling project.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Consent_updates_exact_project_in_fresh_scope()
    {
        using var fixture = new Fixture();
        var ownerForCallbacks = fixture.Owner;

        fixture.Apps.Call = (method, args) =>
        {
            Assert.Equal("SetAiDiagnosticEgressConsentAsync", method?.Name);
            Assert.Equal(5, args!.Length);
            Assert.Equal(fixture.App.Id, args[0]);
            Assert.Equal(ownerForCallbacks, args[1]);
            Assert.Equal(fixture.Selected.Id, args[2]);
            Assert.Equal(true, args[3]);
            Assert.True(((CancellationToken)args[4]!).CanBeCanceled);

            return Task.FromResult(true);
        };
        await fixture.Invoke("ConsentAsync", true);

        Assert.Equal(true, fixture.Get("_consented"));
        Assert.False(fixture.Other.Configuration!.AiDiagnosticEgressConsented);
        Assert.Contains("consent saved", fixture.Message);
    }

    /// <summary>
    ///     A rejection or uncertain response does not optimistically change consent or reveal
    ///     exceptions.
    /// </summary>
    /// <param name="uncertain">Whether the fixture leaves request admission uncertain.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_failure_preserves_saved_setting(bool uncertain)
    {
        using var fixture = new Fixture();

        fixture.Apps.Call = (_, _) =>
            uncertain ? Task.FromException<bool>(new Exception("private-secret")) : Task.FromResult(false);
        await fixture.Invoke("ConsentAsync", true);

        Assert.Equal(false, fixture.Get("_consented"));
        Assert.Contains(uncertain ? "Analysis could not be updated" : "Consent", fixture.Message);
        Assert.DoesNotContain("private", fixture.Message);
        Assert.False((bool)fixture.Get("_busy")!);
    }

    /// <summary>
    ///     All cancellation outcomes read authoritative saved state without requiring AI enablement or
    ///     consent.
    /// </summary>
    /// <param name="outcome">Configured or expected operation outcome.</param>
    /// <param name="status">Optional analysis state under test.</param>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(DeploymentAnalysisCancellationResult.Cancelled, AiAnalysisStatus.Cancelled, "canceled")]
    [InlineData(DeploymentAnalysisCancellationResult.AlreadyFinished, AiAnalysisStatus.Completed, "already finished")]
    [InlineData(DeploymentAnalysisCancellationResult.NotFound, null, "no longer available")]
    public async Task Cancel_reads_saved_outcome(DeploymentAnalysisCancellationResult outcome, AiAnalysisStatus? status,
        string message)
    {
        using var fixture = new Fixture();
        var ownerForCallbacks = fixture.Owner;
        var deploymentForCallbacks = fixture.Deployment.Id;
        var active = fixture.View(AiAnalysisStatus.Running);

        fixture.Set("_latest", active);
        var saved = status is null ? null : fixture.View(status.Value);

        fixture.Analyses.Call = (method, args) =>
        {
            Assert.Equal(ownerForCallbacks, args![0]);
            Assert.Equal(deploymentForCallbacks, args[1]);

            if (method?.Name == "CancelAsync")
            {
                Assert.Equal(active.Id, args[2]);

                return Task.FromResult(outcome);
            }

            Assert.Equal("GetLatestAsync", method?.Name);

            return Task.FromResult(saved);
        };
        await fixture.Invoke("CancelAsync");

        Assert.Same(saved, fixture.Get("_latest"));
        Assert.Contains(message, fixture.Message);
    }

    /// <summary>
    ///     Uncertain cancellation can be retried for the same identity and cannot expose provider
    ///     details.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Uncertain_cancel_retries_same_identity()
    {
        using var fixture = new Fixture();
        var active = fixture.View(AiAnalysisStatus.Queued);

        fixture.Set("_latest", active);
        var calls = 0;

        fixture.Analyses.Call = (method, args) =>
        {
            Assert.Equal("CancelAsync", method?.Name);
            Assert.Equal(active.Id, args![2]);

            calls++;

            return Task.FromException<DeploymentAnalysisCancellationResult>(new Exception("private-secret"));
        };
        await fixture.Invoke("CancelAsync");
        await fixture.Invoke("CancelAsync");

        Assert.Equal(2, calls);
        Assert.Same(active, fixture.Get("_latest"));
        Assert.Contains("could not be updated", fixture.Message);
        Assert.DoesNotContain("private", fixture.Message);
    }

    /// <summary>
    ///     Consent and cancellation ignore late responses after selection changes, suppressing
    ///     duplicate actions meanwhile.
    /// </summary>
    /// <param name="consent">Legacy preference value used to verify automatic collection behavior.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_deployment_discards_action_feedback(bool consent)
    {
        using var fixture = new Fixture();

        fixture.Set("_latest", fixture.View(AiAnalysisStatus.Queued));
        var consentResponse = new TaskCompletionSource<bool>();
        var cancelResponse = new TaskCompletionSource<DeploymentAnalysisCancellationResult>();
        var calls = 0;

        fixture.Apps.Call = (_, _) =>
        {
            calls++;

            return consentResponse.Task;
        };
        fixture.Analyses.Call = (_, _) =>
        {
            calls++;

            return cancelResponse.Task;
        };
        var method = consent ? "ConsentAsync" : "CancelAsync";
        var args = consent ? new object?[] { true } : [];
        var action = fixture.Invoke(method, args);

        await fixture.Invoke(method, args);

        Assert.Equal(1, calls);

        fixture.Deployment.Id = Guid.NewGuid();
        fixture.Parameter("DeploymentId", (Guid?)fixture.Deployment.Id);
        fixture.Set("_identity", (fixture.Owner, fixture.App.Id, (Guid?)fixture.Deployment.Id));
        consentResponse.SetResult(true);
        cancelResponse.SetResult(DeploymentAnalysisCancellationResult.Cancelled);
        await action;

        Assert.Null(fixture.Get("_message"));
        Assert.Equal(false, fixture.Get("_consented"));
    }

    /// <summary>
    ///     Page state and isolated DI ports without provider, renderer or database activity.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Page reflection scope.</summary>
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>Real handler implementation.</summary>
        private readonly DeploymentAnalysisSection _component = new();

        /// <summary>Independent service scope provider.</summary>
        private readonly ServiceProvider _services;

        /// <summary>
        ///     Seeds selection and fresh-scope dependencies.
        /// </summary>
        public Fixture()
        {
            Deployment = new Deployment { CsProjectId = Selected.Id, Status = DeploymentStatus.Failed };
            Selected.Deployments.Add(Deployment);
            App = new Domain.Entities.Projects.Application
            {
                Name = "fixture",
                SourceType = SourceType.Local,
                SourcePathOrUrl = "fixture",
                CsProjects = [Other, Selected]
            };
            typeof(DeploymentAnalysisSection).GetProperty("ProjectId")!.SetValue(_component, App.Id);
            Parameter("OwnerId", Owner);
            Parameter("DeploymentId", (Guid?)Deployment.Id);
            Parameter("CsProjectId", Selected.Id);
            Set("_identity", (Owner, App.Id, (Guid?)Deployment.Id));
            var apps = DispatchProxy.Create<IApplicationService, OperationalLoggingTests.PortProxy>();
            var analyses = DispatchProxy.Create<IDeploymentAnalysisService, OperationalLoggingTests.PortProxy>();

            Apps = Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(apps);
            Analyses = Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(analyses);
            _services = new ServiceCollection().AddScoped(_ => apps).AddScoped(_ => analyses).BuildServiceProvider();
            typeof(DeploymentAnalysisSection).GetProperty("Scopes", Flags | BindingFlags.Public)!.SetValue(_component,
                _services.GetRequiredService<IServiceScopeFactory>());
            Parameter("Logger", NullLogger<DeploymentAnalysisSection>.Instance);
        }

        /// <summary>Application use-case probe.</summary>
        public OperationalLoggingTests.PortProxy Apps { get; }

        /// <summary>Analysis use-case probe.</summary>
        public OperationalLoggingTests.PortProxy Analyses { get; }

        /// <summary>Owning user identity.</summary>
        public Guid Owner { get; } = Guid.NewGuid();

        /// <summary>Sibling configured project comes first.</summary>
        public CsProject Other { get; } = new() { Configuration = new Configuration { DotNetVersion = "net10.0" } };

        /// <summary>Actual deployment project.</summary>
        public CsProject Selected { get; } = new() { Configuration = new Configuration { DotNetVersion = "net10.0" } };

        /// <summary>Current failed deployment.</summary>
        public Deployment Deployment { get; }

        /// <summary>Owner application.</summary>
        public Domain.Entities.Projects.Application App { get; }

        /// <summary>Safe page feedback.</summary>
        public string Message => (string)Get("_message")!;

        /// <summary>
        ///     Releases owned fixture services, connections and background resources.
        /// </summary>
        public void Dispose()
        {
            _services.Dispose();
        }

        /// <summary>
        ///     Invokes the real handler.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        public Task Invoke(string method, params object?[] args)
        {
            return (Task)typeof(DeploymentAnalysisSection).GetMethod(method, Flags)!.Invoke(_component, args)!;
        }

        /// <summary>
        ///     Supplies the shared component's deployment parameters or injected port.
        /// </summary>
        /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
        /// <param name="value">Fixture member value to assign.</param>
        public void Parameter(string name, object value)
        {
            typeof(DeploymentAnalysisSection).GetProperty(name, Flags | BindingFlags.Public)!.SetValue(_component,
                value);
        }

        /// <summary>
        ///     Seeds presentation state.
        /// </summary>
        /// <param name="field">Private field name accessed by the fixture.</param>
        /// <param name="value">Fixture member value to assign.</param>
        public void Set(string field, object value)
        {
            typeof(DeploymentAnalysisSection).GetField(field, Flags)!.SetValue(_component, value);
        }

        /// <summary>
        ///     Reads presentation state.
        /// </summary>
        /// <param name="field">Private field name accessed by the fixture.</param>
        /// <returns>The selected private field value, which may be null.</returns>
        public object? Get(string field)
        {
            return typeof(DeploymentAnalysisSection).GetField(field, Flags)!.GetValue(_component);
        }

        /// <summary>
        ///     Creates safe synthetic persisted state.
        /// </summary>
        /// <param name="status">Analysis state under test.</param>
        /// <returns>The configured deployment analysis view instance or response for this scenario.</returns>
        public DeploymentAnalysisView View(AiAnalysisStatus status)
        {
            return new DeploymentAnalysisView(Guid.NewGuid(), Deployment.Id, status, AiAnalysisTrigger.Manual, null, [],
                [], null,
                DateTimeOffset.UtcNow, null);
        }
    }
}
