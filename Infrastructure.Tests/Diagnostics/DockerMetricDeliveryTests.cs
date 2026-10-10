using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using FluentAssertions;
using Infrastructure.Diagnostics;
using Infrastructure.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Verifies Docker live refresh is independent of durable metric sampling and delivery failures.</summary>
public sealed class DockerMetricDeliveryTests
{
    /// <summary>Each observation reaches viewers while only one sample per interval enters history.</summary>
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
        history.Events.Should().HaveCount(2);
        history.Events.Last().Metrics!.Single(p => p.Unit == "cores").Value.Should().Be(0.6);
        viewers.Remove("owner");
        await delivery.ObserveAsync(project, deployment, "web", new DockerMetricsLine("80%", "100MiB / 200MiB"),
            CancellationToken.None);
        live.Values.Should().HaveCount(62);
        history.Events.Should().HaveCount(2);
    }

    /// <summary>A transient UI failure cannot terminate collection; live values are centrally redacted.</summary>
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
        history.Events.Should().ContainSingle();
    }

    /// <summary>Deterministic history sampling clock.</summary>
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    /// <summary>Records only durable sampling calls.</summary>
    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher
    {
        public List<DeploymentDiagnosticEvent> Events { get; } = [];

        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken token = default)
        {
            Events.Add(diagnosticEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Records redacted live observations and simulates a transient delivery failure.</summary>
    private sealed class RecordingLive : ILogStreamer
    {
        public bool FailNext { get; set; }
        public List<(string Cpu, string Memory)> Values { get; } = [];

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

        public Task StreamTerminalLogAsync(DeploymentTerminalLog terminalLog,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task StreamTerminalNoticeAsync(Guid project, string message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}