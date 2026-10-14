using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Logging;
using FluentAssertions;
using Infrastructure.Diagnostics.Runtime;
using Infrastructure.Docker.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Infra.Diagnostics.Docker;

/// <summary>
///     Verifies Docker live refresh is independent of durable metric sampling and delivery
///     failures.
/// </summary>
public sealed class DockerMetricDeliveryTests
{
    /// <summary>
    ///     Each observation reaches viewers while only one sample per interval enters history.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Live_updates_are_frequent_and_history_stays_sampled()
    {
        var clock = new TestClock();
        var viewers = new DeploymentRuntimeViewers(clock);
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var live = new RecordingLive();
        var history = new RecordingPublisher();
        var delivery = new DockerMetricDelivery(history, live, new DiagnosticRedactor(), viewers, clock, 60,
            NullLogger.Instance);
        for (var second = 0; second <= 60; second++)
        {
            viewers.Renew("owner", project, deployment);
            await delivery.ObserveAsync(project, deployment, "web",
                new DockerMetricsLine($"{second}%", "100MiB / 200MiB"), CancellationToken.None);
            clock.Now += TimeSpan.FromSeconds(1);
        }

        live.Values.Should().HaveCount(61);
        live.Values.Last().Cpu.Should().Be("60%");
        live.Snapshots.Should().HaveCount(61);
        live.Snapshots.Should().OnlyContain(snapshot => snapshot.ProjectId == project &&
                                                        snapshot.DeploymentId == deployment &&
                                                        snapshot.ResourceName == "web");
        live.Snapshots.Last().Timestamp.Should().Be(clock.Now.AddSeconds(-1));
        live.Snapshots.Last().Samples.Single(p => p.Unit == "cores").Value.Should().Be(0.6);
        live.Snapshots.Last().Samples.Single(p => p.Name == "automate_memory_used_bytes").Value.Should().Be(104857600);
        history.Events.Should().HaveCount(2);
        history.Events.Last().Metrics!.Single(p => p.Unit == "cores").Value.Should().Be(0.6);
        viewers.Remove("owner");
        await delivery.ObserveAsync(project, deployment, "web", new DockerMetricsLine("80%", "100MiB / 200MiB"),
            CancellationToken.None);
        live.Values.Should().HaveCount(62);
        live.Snapshots.Should().HaveCount(62);
        history.Events.Should().HaveCount(2);
    }

    /// <summary>
    ///     A transient UI failure cannot terminate collection; live values are centrally redacted.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Live_delivery_recovers_and_redacts_before_transport()
    {
        var clock = new TestClock();
        var viewers = new DeploymentRuntimeViewers(clock);
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        viewers.Renew("owner", project, deployment);
        var live = new RecordingLive { FailNext = true };
        var history = new RecordingPublisher();
        var delivery = new DockerMetricDelivery(history, live, new DiagnosticRedactor(), viewers, clock, 60,
            NullLogger.Instance);
        await delivery.ObserveAsync(project, deployment, "web", new DockerMetricsLine("1%", "100MiB"),
            CancellationToken.None);
        await delivery.ObserveAsync(project, deployment, "web", new DockerMetricsLine("2%", "password=private-value"),
            CancellationToken.None);
        live.Values.Should().ContainSingle();
        live.Values.Single().Cpu.Should().Be("2%");
        live.Values.Single().Memory.Should().NotContain("private-value");
        live.Snapshots.Should().HaveCount(2);
        live.Snapshots.Last().Samples.Should().OnlyContain(sample => double.IsFinite(sample.Value));
        history.Events.Should().ContainSingle();
    }

    /// <summary>
    ///     Deterministic history sampling clock.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>Controlled UTC clock value.</summary>
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        ///     Returns the controlled UTC clock value.
        /// </summary>
        /// <returns>The fixture-controlled UTC time.</returns>
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    /// <summary>
    ///     Records only durable sampling calls.
    /// </summary>
    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher
    {
        /// <summary>Synthetic events captured or supplied by this fixture.</summary>
        public List<DeploymentDiagnosticEvent> Events { get; } = [];

        /// <summary>
        ///     Records normalized fixture observations and reports the configured publication outcome.
        /// </summary>
        /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken token = default)
        {
            Events.Add(diagnosticEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     Records redacted live observations and simulates a transient delivery failure.
    /// </summary>
    private sealed class RecordingLive : ILogStreamer
    {
        /// <summary>Whether to fail the next operation only.</summary>
        public bool FailNext { get; set; }

        /// <summary>Captured compatibility CPU/memory values.</summary>
        public List<(string Cpu, string Memory)> Values { get; } = [];

        /// <summary>Typed observations consumed by pages after saved metric recovery.</summary>
        public List<DeploymentRuntimeMetricSnapshot> Snapshots { get; } = [];

        /// <summary>Captures each redacted typed observation independently of durable history admission.</summary>
        /// <param name="snapshot">Provider-timestamped numeric observation with deployment ownership.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture transport.</param>
        /// <returns>An already completed task after recording the observation.</returns>
        public Task StreamDeploymentMetricsAsync(DeploymentRuntimeMetricSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Captures metric display values and optionally fails the next live-delivery call.
        /// </summary>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="container">Recorded container identity to select; ignored by this fixture.</param>
        /// <param name="cpu">Provider CPU display value.</param>
        /// <param name="memory">Provider memory display value.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        /// <exception cref="InvalidOperationException">
        ///     The fixture injects a configured failure or detects an unexpected operation.
        /// </exception>
        public Task StreamContainerMetricsAsync(Guid project, string container, string cpu, string memory,
            CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("disconnected");
            }

            Values.Add((cpu, memory));
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Completes immediately without executing live stream terminal log work.
        /// </summary>
        /// <param name="terminalLog">Redacted deployment output with channel and replay identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task StreamTerminalLogAsync(DeploymentTerminalLog terminalLog,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Completes immediately without executing live stream terminal notice work.
        /// </summary>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="message">
        ///     Display message; secret-bearing provider text must not be supplied; ignored by this fixture.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task StreamTerminalNoticeAsync(Guid project, string message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
