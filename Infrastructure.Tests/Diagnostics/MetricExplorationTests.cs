using Application.Abstractions.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Exercises window limits and durable metric lookup recovery without cloud calls.</summary>
public sealed class MetricExplorationTests
{
    /// <summary>Presets and custom limits use UTC calendar years, including leap dates.</summary>
    [Fact]
    public void Windows_validate_calendar_bounds_and_keep_charts_bounded()
    {
        var now = new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero);
        foreach (var preset in new[] { "10m", "30m", "1h", "6h", "24h", "7d", "30d", "90d", "1y", "5y" })
        {
            var range = MetricTimeRange.Preset(preset, now);
            range.Validate(now);
            var buckets = Enumerable.Range(0, 10000).Select(i => range.Bucket(range.Start.AddTicks(
                (range.End - range.Start).Ticks / 10000 * i))).Distinct().Count();
            Assert.InRange(buckets, 1, 240);
        }

        Assert.Equal(now.AddYears(-5), MetricTimeRange.Preset("5y", now).Start);
        new MetricTimeRange(now.AddMinutes(-5), now).Validate(now);
        Assert.Throws<ArgumentException>(() => new MetricTimeRange(now.AddMinutes(-4), now).Validate(now));
        Assert.Throws<ArgumentException>(() => new MetricTimeRange(now.AddYears(-5).AddDays(-1), now).Validate(now));
        Assert.Throws<ArgumentException>(() =>
            new MetricTimeRange(now.AddMinutes(-10), now.AddSeconds(1)).Validate(now));
    }

    /// <summary>Numeric observations retain counts/container identities after duplicate delivery, restart and lookup loss.</summary>
    [Fact]
    public async Task Indexed_metrics_preserve_samples_filtering_and_restart_recovery()
    {
        var path = Path.Combine(Path.GetTempPath(), "automate-metrics-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        var range = new MetricTimeRange(now.AddMinutes(-10), now);
        var sample = Entry(tenant, project, deployment, now.AddMinutes(-1), "web", .1);
        try
        {
            using (var archive = Create(path))
            {
                await archive.AppendAsync(sample, default);
                await archive.AppendAsync(sample, default);
                await archive.AppendAsync(Entry(tenant, project, deployment, sample.Event.TimestampUtc, "web", .9),
                    default);
                await archive.AppendAsync(Entry(tenant, project, deployment, sample.Event.TimestampUtc, "database", .7),
                    default);
                await archive.AppendAsync(Entry(tenant, project, deployment, now.AddDays(-40), "web", 99), default);
                var page = await archive.ReadIndexedMetricsAsync(tenant, project, deployment, range, "web", default);
                var row = Assert.Single(page.Items);
                Assert.Equal(2, row.Samples);
                Assert.Equal(.5, row.Average, 8);
                Assert.Equal(.1, row.Minimum);
                Assert.Equal(.9, row.Maximum);
                Assert.Null(page.Availability);
                Assert.Empty(
                    (await archive.ReadIndexedMetricsAsync(Guid.NewGuid(), project, deployment, range, null, default))
                    .Items);
            }

            var index = Directory.GetDirectories(path, "metric-index", SearchOption.AllDirectories).Single();
            Directory.Delete(index, true);
            using var reopened = Create(path);
            var rebuilt = await reopened.ReadIndexedMetricsAsync(tenant, project, deployment, range, null, default);
            Assert.Equal(2, rebuilt.Items.Count);
            Assert.Equal(3, rebuilt.Items.Sum(p => p.Samples));
            var old = await reopened.ReadIndexedMetricsAsync(tenant, project, deployment,
                new MetricTimeRange(now.AddYears(-5), now), "web", default);
            Assert.Equal(3, old.Items.Sum(p => p.Samples));
            await reopened.DeleteProjectAsync(tenant, project, default);
            Assert.Empty((await reopened.ReadIndexedMetricsAsync(tenant, project, deployment, range, null, default))
                .Items);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    /// <summary>Derived lookup corruption is repairable without modifying acknowledged source segments.</summary>
    [Fact]
    public async Task Damaged_lookup_is_unavailable_then_rebuilt_from_checksums()
    {
        var path = Path.Combine(Path.GetTempPath(), "automate-metric-recovery-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        try
        {
            using var archive = Create(path);
            await archive.AppendAsync(Entry(tenant, project, deployment, now.AddMinutes(-1), "web", .2), default);
            var range = new MetricTimeRange(now.AddMinutes(-10), now);
            await archive.ReadIndexedMetricsAsync(tenant, project, deployment, range, null, default);
            var reference = Directory.GetFiles(path, "*.ref", SearchOption.AllDirectories).Single();
            await File.WriteAllTextAsync(reference, "damaged lookup");
            var partial = await archive.ReadIndexedMetricsAsync(tenant, project, deployment, range, null, default);
            Assert.Empty(partial.Items);
            Assert.Contains("rebuilt", partial.Availability);
            Assert.Single((await archive.ReadIndexedMetricsAsync(tenant, project, deployment, range, null, default))
                .Items);
            Assert.Single(Directory.GetFiles(path, "*.segment", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    /// <summary>Isolated checksummed volume using current redaction policy.</summary>
    private static DiskDeploymentArchive Create(string path)
    {
        return new DiskDeploymentArchive(
            Options.Create(new DiskSpoolOptions { Directory = path }), new DiagnosticRedactor());
    }

    /// <summary>Typed recorded container sample; diagnostic prose never belongs in the metric lookup.</summary>
    private static DeploymentLogEnvelope Entry(Guid tenant, Guid project, Guid deployment, DateTimeOffset time,
        string container, double value)
    {
        var id = Guid.NewGuid();
        return new DeploymentLogEnvelope(id, tenant, time.UtcTicks, time, time.AddDays(30),
            new DeploymentDiagnosticEvent(project, deployment, DeploymentDiagnosticSource.DockerContainer,
                DeploymentDiagnosticKind.Metric, DeploymentDiagnosticSeverity.Information, time, "",
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, container),
                Metrics: [new DeploymentMetricSample("automate_cpu_usage_cores", value, "cores")], EventId: id),
            container);
    }
}