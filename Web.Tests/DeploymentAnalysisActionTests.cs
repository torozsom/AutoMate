using System.Reflection;
using Application.Abstractions.Ai;
using Application.Data.Apps;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Web.Components.Pages;
using Xunit;

namespace Web.Tests;

/// <summary>Exercises real owner consent/cancel handlers with controlled Application ports.</summary>
public sealed class DeploymentAnalysisActionTests
{
    /// <summary>Consent uses the selected deployment's exact project and updates no sibling project.</summary>
    [Fact]
    public async Task Consent_updates_exact_project_in_fresh_scope()
    {
        using var fixture = new Fixture();
        fixture.Apps.Call = (method, args) =>
        {
            Assert.Equal("SetAiDiagnosticEgressConsentAsync", method?.Name);
            Assert.Equal(5, args!.Length);
            Assert.Equal(fixture.App.Id, args[0]);
            Assert.Equal(fixture.Owner, args[1]);
            Assert.Equal(fixture.Selected.Id, args[2]);
            Assert.Equal(true, args[3]);
            Assert.True(((CancellationToken)args[4]!).CanBeCanceled);
            return Task.FromResult(true);
        };
        await fixture.Invoke("SetAiConsentAsync", true);
        Assert.True(fixture.Selected.Configuration!.AiDiagnosticEgressConsented);
        Assert.False(fixture.Other.Configuration!.AiDiagnosticEgressConsented);
        Assert.Contains("consent saved", fixture.Message);
    }

    /// <summary>A rejection or uncertain response does not optimistically change consent or reveal exceptions.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_failure_preserves_saved_setting(bool uncertain)
    {
        using var fixture = new Fixture();
        fixture.Apps.Call = (_, _) =>
            uncertain ? Task.FromException<bool>(new Exception("private-secret")) : Task.FromResult(false);
        await fixture.Invoke("SetAiConsentAsync", true);
        Assert.False(fixture.Selected.Configuration!.AiDiagnosticEgressConsented);
        Assert.Contains("Consent", fixture.Message);
        Assert.DoesNotContain("private", fixture.Message);
        Assert.False((bool)fixture.Get("_analysisBusy")!);
    }

    /// <summary>All cancellation outcomes read authoritative saved state without requiring AI enablement or consent.</summary>
    [Theory]
    [InlineData(DeploymentAnalysisCancellationResult.Cancelled, AiAnalysisStatus.Cancelled, "canceled")]
    [InlineData(DeploymentAnalysisCancellationResult.AlreadyFinished, AiAnalysisStatus.Completed, "already finished")]
    [InlineData(DeploymentAnalysisCancellationResult.NotFound, null, "no longer available")]
    public async Task Cancel_reads_saved_outcome(DeploymentAnalysisCancellationResult outcome, AiAnalysisStatus? status,
        string message)
    {
        using var fixture = new Fixture();
        var active = fixture.View(AiAnalysisStatus.Running);
        fixture.Set("_latestAnalysis", active);
        var saved = status is null ? null : fixture.View(status.Value);
        fixture.Analyses.Call = (method, args) =>
        {
            Assert.Equal(fixture.Owner, args![0]);
            Assert.Equal(fixture.Deployment.Id, args[1]);
            if (method?.Name == "CancelAsync")
            {
                Assert.Equal(active.Id, args[2]);
                return Task.FromResult(outcome);
            }

            Assert.Equal("GetLatestAsync", method?.Name);
            return Task.FromResult(saved);
        };
        await fixture.Invoke("CancelAnalysisAsync");
        Assert.Same(saved, fixture.Get("_latestAnalysis"));
        Assert.Contains(message, fixture.Message);
    }

    /// <summary>Uncertain cancellation can be retried for the same identity and cannot expose provider details.</summary>
    [Fact]
    public async Task Uncertain_cancel_retries_same_identity()
    {
        using var fixture = new Fixture();
        var active = fixture.View(AiAnalysisStatus.Queued);
        fixture.Set("_latestAnalysis", active);
        var calls = 0;
        fixture.Analyses.Call = (method, args) =>
        {
            Assert.Equal("CancelAsync", method?.Name);
            Assert.Equal(active.Id, args![2]);
            calls++;
            return Task.FromException<DeploymentAnalysisCancellationResult>(new Exception("private-secret"));
        };
        await fixture.Invoke("CancelAnalysisAsync");
        await fixture.Invoke("CancelAnalysisAsync");
        Assert.Equal(2, calls);
        Assert.Same(active, fixture.Get("_latestAnalysis"));
        Assert.Contains("could not be confirmed", fixture.Message);
        Assert.DoesNotContain("private", fixture.Message);
    }

    /// <summary>
    ///     Consent and cancellation ignore late responses after selection changes, suppressing duplicate actions
    ///     meanwhile.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_deployment_discards_action_feedback(bool consent)
    {
        using var fixture = new Fixture();
        fixture.Set("_latestAnalysis", fixture.View(AiAnalysisStatus.Queued));
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
        var method = consent ? "SetAiConsentAsync" : "CancelAnalysisAsync";
        var args = consent ? new object?[] { true } : [];
        var action = fixture.Invoke(method, args);
        await fixture.Invoke(method, args);
        Assert.Equal(1, calls);
        fixture.Deployment.Id = Guid.NewGuid();
        consentResponse.SetResult(true);
        cancelResponse.SetResult(DeploymentAnalysisCancellationResult.Cancelled);
        await action;
        Assert.Null(fixture.Get("_analysisMessage"));
        Assert.False(fixture.Selected.Configuration!.AiDiagnosticEgressConsented);
    }

    /// <summary>Page state and isolated DI ports without provider, renderer or database activity.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Page reflection scope.</summary>
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>Real handler implementation.</summary>
        private readonly ProjectDetails _component = new();

        /// <summary>Independent service scope provider.</summary>
        private readonly ServiceProvider _services;

        /// <summary>Seeds selection and fresh-scope dependencies.</summary>
        public Fixture()
        {
            Deployment = new Deployment { CsProjectId = Selected.Id, Status = DeploymentStatus.Failed };
            Selected.Deployments.Add(Deployment);
            App = new Domain.Entities.Application
            {
                Name = "fixture", SourceType = SourceType.Local, SourcePathOrUrl = "fixture",
                CsProjects = [Other, Selected]
            };
            typeof(ProjectDetails).GetProperty("ProjectId")!.SetValue(_component, App.Id);
            Set("_app", App);
            Set("_currentUserId", Owner);
            var apps = DispatchProxy.Create<IApplicationService, OperationalLoggingTests.PortProxy>();
            var analyses = DispatchProxy.Create<IDeploymentAnalysisService, OperationalLoggingTests.PortProxy>();
            Apps = (OperationalLoggingTests.PortProxy)apps;
            Analyses = (OperationalLoggingTests.PortProxy)analyses;
            _services = new ServiceCollection().AddScoped(_ => apps).AddScoped(_ => analyses).BuildServiceProvider();
            typeof(ProjectDetails).GetProperty("ScopeFactory", Flags)!.SetValue(_component,
                _services.GetRequiredService<IServiceScopeFactory>());
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
        public Domain.Entities.Application App { get; }

        /// <summary>Safe page feedback.</summary>
        public string Message => (string)Get("_analysisMessage")!;

        /// <inheritdoc />
        public void Dispose()
        {
            _services.Dispose();
        }

        /// <summary>Invokes the real handler.</summary>
        public Task Invoke(string method, params object?[] args)
        {
            return (Task)typeof(ProjectDetails).GetMethod(method, Flags)!.Invoke(_component, args)!;
        }

        /// <summary>Seeds presentation state.</summary>
        public void Set(string field, object value)
        {
            typeof(ProjectDetails).GetField(field, Flags)!.SetValue(_component, value);
        }

        /// <summary>Reads presentation state.</summary>
        public object? Get(string field)
        {
            return typeof(ProjectDetails).GetField(field, Flags)!.GetValue(_component);
        }

        /// <summary>Creates safe synthetic persisted state.</summary>
        public DeploymentAnalysisView View(AiAnalysisStatus status)
        {
            return new DeploymentAnalysisView(Guid.NewGuid(), Deployment.Id, status, AiAnalysisTrigger.Manual, null, [],
                [], null,
                DateTimeOffset.UtcNow, null);
        }
    }
}