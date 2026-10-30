using System.Text.Json;
using Application.Abstractions.Ai.Execution;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Data.Apps;
using Domain.Enums.Ai;
using Domain.Enums.Projects;

namespace Test.App;

/// <summary>Protects optional metadata and paging fields at JSON transport boundaries.</summary>
public sealed class ProjectionContractTests
{
    /// <summary>Known estimates survive transport while unknown legacy estimates remain null.</summary>
    [Fact]
    public void Analysis_cost_metadata_preserves_known_and_unknown_values()
    {
        var legacy = new DeploymentAnalysisView(Guid.NewGuid(), Guid.NewGuid(), AiAnalysisStatus.Completed,
            AiAnalysisTrigger.Manual, "safe", [], [], null, DateTimeOffset.UtcNow, null);
        var unknown = RoundTrip(legacy);
        Assert.Null(unknown.EstimatedCost);
        Assert.Null(unknown.CostCurrency);

        var estimated = RoundTrip(legacy with { EstimatedCost = 0.0123m, CostCurrency = "USD" });
        Assert.Equal(0.0123m, estimated.EstimatedCost);
        Assert.Equal("USD", estimated.CostCurrency);
    }

    /// <summary>History continuations and storage-region compatibility fields survive JSON readback.</summary>
    [Fact]
    public void History_and_preferences_preserve_transport_metadata()
    {
        var page = RoundTrip(new TelemetryLogPage([], "before", "after", null, true));
        Assert.Equal("before", page.EarlierCursor);
        Assert.Equal("after", page.LaterCursor);
        Assert.True(page.CanAdvanceCursor);

        var preferences = RoundTrip(new DeploymentTelemetryPreferences(true, false, true, "eu"));
        Assert.True(preferences.RuntimeEnabled);
        Assert.False(preferences.ManagedConsent);
        Assert.True(preferences.ManagedService);
        Assert.Equal("eu", preferences.ProcessingRegion);
    }

    /// <summary>Numeric paging retains its frozen window and does not lose page coordinates in transport.</summary>
    [Fact]
    public void Metric_pages_preserve_paging_and_window()
    {
        var end = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var range = new MetricTimeRange(end.AddHours(-1), end);
        var page = RoundTrip(new MetricExplorationResult([], [], 56, 3, 25, range));
        Assert.Equal(3, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Equal(56, page.Total);
        Assert.Equal(range, page.Range);
    }

    /// <summary>Inventory timestamps and resource units retain their original wire values.</summary>
    [Fact]
    public void Workspace_projections_preserve_dates_and_units()
    {
        var saved = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var project = RoundTrip(new ProjectInventoryRow(Guid.NewGuid(), "fixture", "fixture.csproj",
            SourceType.Local, 1, 1, saved, null, null));
        Assert.Equal(saved, project.SavedAt);
        Assert.Null(project.LatestAt);
        var resource = RoundTrip(new WorkspaceResource(saved, "automate_memory_used_bytes", "bytes",
            2, 1024, 512, 1536, false));
        Assert.Equal("bytes", resource.Unit);
        Assert.Equal(1024, resource.Average);
    }

    /// <summary>Exercises the default serializer used by HTTP projections without changing contract options.</summary>
    private static T RoundTrip<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
               ?? throw new InvalidOperationException("A projection must deserialize to a value.");
    }
}
