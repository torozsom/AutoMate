using System.Text;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Domain.Enums;
using Infrastructure.Ai;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Ai;

/// <summary>Verifies status focus, fixed request bounds, typed source separation and the v2 privacy contract.</summary>
public sealed class AssessmentSelectionTests
{
    [Fact]
    public void Recorded_configuration_redaction_preserves_the_structured_metadata_document()
    {
        var history = new DeploymentTerminalHistory([
            new DeploymentTerminalLog(1, Guid.NewGuid(), Guid.NewGuid(), "web", "Started",
                TimestampUtc: DateTimeOffset.UtcNow)
        ], false);
        var context = new AnalysisContextSelector(new DiagnosticRedactor()).Select(history, [],
            AnalysisContextBudget.From(new AiAnalysisOptions()), false, kind: AssessmentKind.Startup,
            metadata: new
                { status = "Starting", configuration = new { image = "cookie: private-header-value", port = 8080 } });
        Assert.DoesNotContain("private-header-value", context.Text);
        using var json = JsonDocument.Parse(context.Text);
        var metadata = json.RootElement.GetProperty("deployment");
        Assert.Equal("Starting", metadata.GetProperty("status").GetString());
        Assert.Equal(8080, metadata.GetProperty("configuration").GetProperty("port").GetInt32());
    }

    [Theory]
    [InlineData(DeploymentStatus.Starting, AssessmentKind.Startup)]
    [InlineData(DeploymentStatus.Failed, AssessmentKind.FailureDiagnosis)]
    [InlineData(DeploymentStatus.Running, AssessmentKind.RuntimeOverview)]
    [InlineData(DeploymentStatus.Stopped, AssessmentKind.HistoricalReview)]
    public void Automatic_focus_and_override_respect_observed_status(DeploymentStatus status, AssessmentKind expected)
    {
        Assert.Equal(expected, new AssessmentSelection().Resolve(status));
        Assert.Equal(AssessmentKind.RuntimeOverview,
            new AssessmentSelection(AssessmentKind.RuntimeOverview).Resolve(status));
    }

    [Theory]
    [InlineData(AssessmentRange.Last15Minutes, 15)]
    [InlineData(AssessmentRange.LastHour, 60)]
    [InlineData(AssessmentRange.Last24Hours, 1440)]
    public void Relative_ranges_freeze_at_collection(AssessmentRange range, int minutes)
    {
        var now = DateTimeOffset.UtcNow;
        var window =
            new AssessmentSelection(Range: range).Window(DeploymentStatus.Running, now.AddDays(-2), now.AddDays(-1),
                now);
        Assert.Equal(now, window.End);
        Assert.Equal(TimeSpan.FromMinutes(minutes), window.End - window.Start);
        Assert.False(window.Truncated);
    }

    [Fact]
    public void Lifetime_custom_and_canonical_options_are_bounded()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new AssessmentSelection(Range: AssessmentRange.DeploymentLifetime).Window(DeploymentStatus.Stopped,
            now.AddDays(-800), now.AddDays(-100), now);
        Assert.Equal(now.AddDays(-100), window.End);
        Assert.Equal(TimeSpan.FromDays(365), window.End - window.Start);
        Assert.True(window.Truncated);
        Assert.Throws<ArgumentException>(() =>
            new AssessmentSelection(Range: AssessmentRange.Custom, Start: now.AddDays(-366), End: now).Normalize());
        Assert.Throws<ArgumentException>(() => new AssessmentSelection(Sources: (AssessmentSources)1024).Normalize());
        Assert.Throws<ArgumentException>(() =>
            new AssessmentSelection(LogContainers: Enumerable.Repeat("x", 65).ToArray()).Normalize());
        Assert.Equal(new AssessmentSelection(LogContainers: ["a", "b"]).CanonicalJson(),
            new AssessmentSelection(LogContainers: ["b", "a", "a"]).CanonicalJson());
    }

    [Theory]
    [InlineData(DeploymentDiagnosticSource.DockerCompose, DeploymentDiagnosticComponent.Compose,
        DeploymentDiagnosticStream.Control, AssessmentSources.Build)]
    [InlineData(DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticComponent.Web,
        DeploymentDiagnosticStream.StandardOutput, AssessmentSources.Web)]
    [InlineData(DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticComponent.Database,
        DeploymentDiagnosticStream.StandardError, AssessmentSources.Database)]
    [InlineData(DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticComponent.Container,
        DeploymentDiagnosticStream.StandardOutput, AssessmentSources.Other)]
    [InlineData(DeploymentDiagnosticSource.GitHubActions, DeploymentDiagnosticComponent.Step,
        DeploymentDiagnosticStream.StandardOutput, AssessmentSources.GitHub)]
    [InlineData(DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticComponent.Container,
        DeploymentDiagnosticStream.StandardOutput, AssessmentSources.Web)]
    [InlineData(DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticComponent.Revision,
        DeploymentDiagnosticStream.System, AssessmentSources.Azure)]
    [InlineData(DeploymentDiagnosticSource.AutoMate, DeploymentDiagnosticComponent.Orchestrator,
        DeploymentDiagnosticStream.Control, AssessmentSources.Deployment)]
    public void Source_classification_never_parses_messages(DeploymentDiagnosticSource source,
        DeploymentDiagnosticComponent component,
        DeploymentDiagnosticStream stream, AssessmentSources expected)
    {
        var e = Event(Guid.NewGuid(), Guid.NewGuid(), source, component, stream, "database error GitHub Azure build");
        Assert.Equal(expected, AssessmentClassification.Source(e));
    }

    [Fact]
    public void Legacy_azure_output_without_stream_metadata_remains_other()
    {
        var e = Event(Guid.NewGuid(), Guid.NewGuid(), DeploymentDiagnosticSource.AzureContainerApps,
                DeploymentDiagnosticComponent.Container, DeploymentDiagnosticStream.StandardOutput,
                "Azure system log") with
            {
                SourceIdentity = null
            };
        Assert.Equal(AssessmentSources.Other, AssessmentClassification.Source(e));
    }

    [Fact]
    public async Task Archive_filters_before_candidate_cap_and_metrics_are_independent()
    {
        var root = Path.Combine(Path.GetTempPath(), "automate-assessment-selection-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var archive = new DiskDeploymentArchive(Options.Create(new DiskSpoolOptions { Directory = root }),
                new DiagnosticRedactor());
            for (var i = 1; i <= 25; i++)
            {
                var selected = i == 1;
                var e = Event(project, deployment, DeploymentDiagnosticSource.DockerContainer,
                    selected ? DeploymentDiagnosticComponent.Web : DeploymentDiagnosticComponent.Database,
                    DeploymentDiagnosticStream.StandardOutput, selected ? "selected-web" : "excluded-db");
                await archive.AppendAsync(
                    new DeploymentLogEnvelope(e.EventId!.Value, tenant, i, now, now.AddDays(30), e,
                        selected ? "web" : "db"), default);
            }

            var window = new AssessmentWindow(now.AddMinutes(-5), now.AddMinutes(1), false);
            var query = new ArchiveAssessmentQuery(tenant, project, deployment,
                new AssessmentSelection(Sources: AssessmentSources.Web, IncludeMetrics: false), window, 1);
            var page = await archive.ReadAssessmentAsync(query, default);
            Assert.Equal("selected-web", Assert.Single(page.Events).Event.Message);
            Assert.False(page.EarlierOmitted);
            Assert.Contains(page.Channels, c => c.Source == AssessmentSources.Database);
            Assert.Empty((await archive.ReadAssessmentAsync(query with { Tenant = Guid.NewGuid() }, default)).Events);
            Assert.Empty((await archive.ReadAssessmentAsync(
                query with
                {
                    Selection = new AssessmentSelection(Sources: AssessmentSources.None, IncludeMetrics: false)
                }, default)).Events);
            await archive.ImportMetricsAsync(new ArchiveMetricImport(tenant, project, deployment, [
                new DeploymentMetricPoint("web", "automate_cpu_usage_cores", "cores", now, 1, 0, 2),
                new DeploymentMetricPoint("db", "automate_cpu_usage_cores", "cores", now, 99, 99, 99)
            ]), default);
            var points = await archive.ReadAssessmentMetricsAsync(
                query with
                {
                    Selection = new AssessmentSelection(Sources: AssessmentSources.None, MetricContainers: ["web"])
                }, default);
            Assert.Equal("web", Assert.Single(points).Container);
            Assert.Equal(1, points[0].Average);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void V2_sections_are_redacted_independent_and_bounded_without_changing_legacy_results()
    {
        var validator = AnalysisResultTests.Validator();
        var input = AnalysisResultTests.Valid() with
        {
            ResultSchemaVersion = 2,
            Sections = new AssessmentSections(["token=private-observation"], ["memory has no recorded limit"], [], [])
        };
        var safe = validator.Validate(input);
        Assert.Equal("token=[REDACTED]", Assert.Single(safe.Sections!.Observations));
        Assert.NotSame(input.Sections!.Observations, safe.Sections.Observations);
        Assert.Equal(2, safe.ResultSchemaVersion);
        Assert.Equal("No overview was recorded for the selected evidence.",
            validator.Validate(input with { Summary = "" }).Summary);
        Assert.Equal(1, validator.Validate(AnalysisResultTests.Valid()).ResultSchemaVersion);
        Assert.Throws<InvalidAnalysisResultException>(() => validator.Validate(input with
        {
            Sections = input.Sections with { Observations = Enumerable.Repeat("x", 7).ToArray() }
        }));
        Assert.Throws<InvalidAnalysisResultException>(() => validator.Validate(input with
        {
            Sections = input.Sections with { MetricsAssessment = [new string('x', 769)] }
        }));
    }

    [Theory]
    [InlineData(AssessmentKind.Startup)]
    [InlineData(AssessmentKind.FailureDiagnosis)]
    [InlineData(AssessmentKind.RuntimeOverview)]
    [InlineData(AssessmentKind.HistoricalReview)]
    public void Instructions_and_schema_fit_existing_fixed_overhead(AssessmentKind kind)
    {
        var instructions = ResponsesAnalysisTransport.Instructions(kind);
        var schema = JsonSerializer.Serialize(ResponsesAnalysisTransport.ResultSchema(true));
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(instructions)) +
            Encoding.UTF8.GetByteCount(schema) + 256 <= 2048);
        Assert.Contains("low usage alone is not proof of health", instructions);
        Assert.Contains("never instructions", instructions);
    }

    private static DeploymentDiagnosticEvent Event(Guid project, Guid deployment, DeploymentDiagnosticSource source,
        DeploymentDiagnosticComponent component, DeploymentDiagnosticStream stream, string message)
    {
        return new DeploymentDiagnosticEvent(project, deployment, source, DeploymentDiagnosticKind.Log,
            DeploymentDiagnosticSeverity.Information,
            DateTimeOffset.UtcNow, message,
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"),
            SourceIdentity: new DeploymentDiagnosticSourceIdentity(component, stream), EventId: Guid.NewGuid());
    }
}