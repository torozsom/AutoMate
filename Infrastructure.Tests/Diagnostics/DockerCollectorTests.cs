using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Abstractions.Logging;
using Docker.DotNet;
using Docker.DotNet.Models;
using Infrastructure.Diagnostics;
using Infrastructure.Docker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Exercises collector framing, owned lifecycle routing and cancellation through the Docker SDK contracts.</summary>
public sealed class DockerCollectorTests
{
    /// <summary>Cancellation must close an idle owned log stream without waiting for its container to exit.</summary>
    [DockerSmokeFact]
    public async Task Real_idle_container_log_subscription_cancels_while_container_keeps_running()
    {
        using var client = new DockerClientConfiguration(new Uri(OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock")).CreateClient();
        var target = DockerOutputTests.Target();
        var name = "automate-idle-cancel-test-" + Guid.NewGuid().ToString("N");
        target = target with { Containers = [target.Containers[0] with { Name = name }] };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var token = new CancellationTokenSource();
        var publisher = new RecordingPublisher();
        var result = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = Environment.GetEnvironmentVariable("AUTOMATE_DOCKER_SMOKE_IMAGE") ?? "busybox:1.37",
            Cmd = ["sh", "-c", "printf 'ready\\n'; sleep 600"],
            Labels = DockerOutputTests.Labels(target, target.Containers[0])
        }, deadline.Token);
        Task? pending = null;
        try
        {
            await client.Containers.StartContainerAsync(result.ID, new ContainerStartParameters(), deadline.Token);
            pending = Collector(client, publisher).MonitorContainerAsync(target, target.Containers[0], token.Token);
            while (!publisher.Events.Any(e => e.Kind == DeploymentDiagnosticKind.Log))
                await Task.Delay(10, deadline.Token);
            token.Cancel();
            await AwaitShutdownAsync(pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True((await client.Containers.InspectContainerAsync(result.ID, deadline.Token)).State.Running);
        }
        finally
        {
            token.Cancel();
            await client.Containers.RemoveContainerAsync(result.ID, new ContainerRemoveParameters { Force = true },
                CancellationToken.None);
            if (pending is not null) await AwaitShutdownAsync(pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    /// <summary>A real idle daemon subscription must finish cancellation without generating any Docker lifecycle mutation.</summary>
    [DockerSmokeFact]
    public async Task Real_idle_daemon_subscription_cancels_without_lifecycle_events()
    {
        using var client = new DockerClientConfiguration(new Uri(OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock")).CreateClient();
        using var token = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Collector(client, new RecordingPublisher()).MonitorDaemonAsync(DockerOutputTests.Target(),
            () => ready.TrySetResult(), token.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        token.Cancel();
        await AwaitShutdownAsync(pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Log collection waits through a creation race, then subscribes to owned output without a failure warning.</summary>
    [Fact]
    public async Task Initial_missing_container_waits_then_collects_owned_output()
    {
        var target = DockerOutputTests.Target();
        var publisher = new RecordingPublisher();
        var logger = new CollectorLogger();
        var inspections = 0;
        var containers = Proxy<IContainerOperations>((method, _) => method.Name switch
        {
            "InspectContainerAsync" when Interlocked.Increment(ref inspections) == 1 =>
                Task.FromException<ContainerInspectResponse>(
                    new DockerContainerNotFoundException(HttpStatusCode.NotFound, "private-body")),
            "InspectContainerAsync" => Task.FromResult(new ContainerInspectResponse
            {
                ID = "owned-container",
                Config = new Config { Labels = DockerOutputTests.Labels(target, target.Containers[0]) },
                State = new ContainerState { Running = true }
            }),
            "GetContainerLogsAsync" => Task.FromResult(
                new MultiplexedStream(new MemoryStream(Frames((1, "2026-10-05T08:00:00Z ready\n"))), true)),
            _ => throw new NotSupportedException(method.Name)
        });
        var client = Proxy<IDockerClient>((_, _) => containers);
        var collector =
            new DockerDiagnosticCollector(client, publisher, new DiagnosticRedactor(), TimeProvider.System, logger);
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = collector.MonitorContainerAsync(target, target.Containers[0], token.Token);
        // The startup wait is informational; wait for actual output rather than using the lifecycle notice as EOF.
        while (!publisher.Events.Any(e => e.Kind == DeploymentDiagnosticKind.Log)) await Task.Delay(10, token.Token);
        token.Cancel();
        await AwaitShutdownAsync(pending);
        Assert.Equal(0, logger.Warnings);
        Assert.Contains(publisher.Events, e => e.Message == "ready\n");
        var waiting = Assert.Single(publisher.Events, e => e.Message.Contains("Waiting for"));
        Assert.Equal(DeploymentDiagnosticSeverity.Information, waiting.Severity);
    }

    /// <summary>Missing containers during Compose startup are a wait state; permission failures still warn.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Metrics_missing_container_is_expected_but_permission_failure_remains_visible(bool missing)
    {
        var target = DockerOutputTests.Target();
        var publisher = new RecordingPublisher();
        var logger = new CollectorLogger();
        var containers = Proxy<IContainerOperations>((_, _) => Task.FromException<ContainerInspectResponse>(missing
            ? new DockerContainerNotFoundException(HttpStatusCode.NotFound, "private-provider-body")
            : new DockerApiException(HttpStatusCode.Forbidden, "private-provider-body")));
        var client = Proxy<IDockerClient>((_, _) => containers);
        var collector =
            new DockerDiagnosticCollector(client, publisher, new DiagnosticRedactor(), TimeProvider.System, logger);
        Assert.Null(await collector.VerifyContainerAsync(target, target.Containers[0], CancellationToken.None));
        Assert.Equal(missing ? 0 : 1, logger.Warnings);
        Assert.Equal(missing ? 0 : 1, publisher.Events.Count);
        Assert.DoesNotContain(publisher.Events, e => e.Message.Contains("private"));
    }

    /// <summary>Multiplexed frames retain separate streams, redact secrets and explicitly report EOF.</summary>
    [Fact]
    public async Task Container_frames_are_redacted_before_confirmation_and_report_eof()
    {
        var target = DockerOutputTests.Target();
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var publisher = new RecordingPublisher();
        var containers = Proxy<IContainerOperations>((method, args) => method.Name switch
        {
            "InspectContainerAsync" => Task.FromResult(new ContainerInspectResponse
            {
                ID = "container-id",
                Config = new Config { Labels = DockerOutputTests.Labels(target, target.Containers[0]) },
                State = new ContainerState { Running = true }
            }),
            "GetContainerLogsAsync" => Task.FromResult(new MultiplexedStream(new MemoryStream(Frames(
                (1, "2026-10-04T08:00:00.123456789Z out ár"),
                (2, "2026-10-04T08:00:00.123456789Z password=private-value\r\n"),
                (1, "víz 😀\r\n2026-10-04T08:00:01Z partial"))), true)),
            _ => throw new NotSupportedException(method.Name)
        });
        var client = Proxy<IDockerClient>((method, _) =>
            method.Name == "get_Containers" ? containers : throw new NotSupportedException(method.Name));
        var pending = Collector(client, publisher).MonitorContainerAsync(target, target.Containers[0], token.Token);
        await publisher.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        token.Cancel();
        await AwaitShutdownAsync(pending);
        var logs = publisher.Events.Where(e => e.Kind == DeploymentDiagnosticKind.Log).ToArray();
        Assert.Equal(3, logs.Length);
        Assert.Contains(logs,
            e => e.Message == "out árvíz 😀\r\n" &&
                 e.SourceIdentity!.Stream == DeploymentDiagnosticStream.StandardOutput);
        Assert.Contains(logs,
            e => e.Message == "password=[REDACTED]\r\n" &&
                 e.SourceIdentity!.Stream == DeploymentDiagnosticStream.StandardError);
        Assert.Contains(logs, e => e.Message == "partial");
        Assert.All(logs, e => Assert.NotNull(e.Cursor));
        Assert.DoesNotContain(publisher.Events, e => e.Message.Contains("private-value"));
    }

    /// <summary>A mismatched inspection never subscribes to another deployment's output.</summary>
    [Fact]
    public async Task Unowned_container_never_opens_logs()
    {
        var target = DockerOutputTests.Target();
        var publisher = new RecordingPublisher();
        var containers = Proxy<IContainerOperations>((method, _) => method.Name == "InspectContainerAsync"
            ? Task.FromResult(new ContainerInspectResponse
                { ID = "foreign", Config = new Config { Labels = new Dictionary<string, string>() } })
            : throw new InvalidOperationException("Must not subscribe to unowned container"));
        var client = Proxy<IDockerClient>((method, _) =>
            method.Name == "get_Containers" ? containers : throw new NotSupportedException());
        await Collector(client, publisher).MonitorContainerAsync(target, target.Containers[0], CancellationToken.None);
        Assert.Single(publisher.Events);
        Assert.Contains("ownership", publisher.Events.Single().Message);
    }

    /// <summary>A failed durable admission replays from the old checkpoint and retains repeated legitimate messages.</summary>
    [Fact]
    public async Task Reconnect_retries_unconfirmed_output_without_collapsing_repeated_lines()
    {
        var target = DockerOutputTests.Target();
        var publisher = new RecordingPublisher { RejectNextLog = true };
        var subscriptions = new List<ContainerLogsParameters>();
        var containers = Proxy<IContainerOperations>((method, args) =>
        {
            if (method.Name == "InspectContainerAsync")
                return Task.FromResult(new ContainerInspectResponse
                {
                    ID = "container-id",
                    Config = new Config { Labels = DockerOutputTests.Labels(target, target.Containers[0]) },
                    State = new ContainerState { Running = true }
                });
            if (method.Name != "GetContainerLogsAsync") throw new NotSupportedException();
            subscriptions.Add((ContainerLogsParameters)args![2]!);
            return Task.FromResult(new MultiplexedStream(new MemoryStream(Frames(
                (1, "2026-10-04T08:00:00.123456789Z same\n2026-10-04T08:00:00.123456789Z same\n"))), true));
        });
        var client = Proxy<IDockerClient>((method, _) =>
            method.Name == "get_Containers" ? containers : throw new NotSupportedException());
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = Collector(client, publisher).MonitorContainerAsync(target, target.Containers[0], token.Token);
        await publisher.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        token.Cancel();
        await AwaitShutdownAsync(pending);
        Assert.Equal(2, subscriptions.Count);
        Assert.All(subscriptions, s => Assert.Null(s.Since));
        var logs = publisher.Events.Where(e => e.Kind == DeploymentDiagnosticKind.Log).ToArray();
        Assert.Equal(2, logs.Length);
        Assert.Equal("same\n", logs[0].Message);
        Assert.Equal("same\n", logs[1].Message);
        Assert.EndsWith("/1", logs[0].Cursor);
        Assert.EndsWith("/2", logs[1].Cursor);
        Assert.NotEqual(logs[0].EventId, logs[1].EventId);
    }

    /// <summary>Supported SDK event callbacks filter before durable publication and cancel without detached tasks.</summary>
    [Fact]
    public async Task Daemon_callbacks_are_filtered_and_receiver_is_awaited_on_shutdown()
    {
        var target = DockerOutputTests.Target();
        var publisher = new RecordingPublisher();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = Proxy<ISystemOperations>((method, args) => method.Name == "MonitorEventsAsync" && args!.Length == 3
            ? ReceiveAsync((IProgress<Message>)args[1]!, (CancellationToken)args[2]!)
            : throw new NotSupportedException());
        var client = Proxy<IDockerClient>((method, _) =>
            method.Name == "get_System" ? system : throw new NotSupportedException());
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = Collector(client, publisher).MonitorDaemonAsync(target, () => { }, token.Token);
        await publisher.Lifecycle.Task.WaitAsync(TimeSpan.FromSeconds(3));
        token.Cancel();
        await AwaitShutdownAsync(pending);
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.Single(publisher.Events,
            e => e.Source == DeploymentDiagnosticSource.DockerDaemon && e.Kind == DeploymentDiagnosticKind.Lifecycle);

        // The custom progress adapter executes synchronously and is governed by the source cancellation token.
        async Task ReceiveAsync(IProgress<Message> progress, CancellationToken cancellation)
        {
            try
            {
                progress.Report(
                    DockerOutputTests.Event(DockerOutputTests.Labels(target, target.Containers[0]), "start"));
                progress.Report(
                    DockerOutputTests.Event(DockerOutputTests.Labels(target, target.Containers[0]), "start"));
                progress.Report(DockerOutputTests.Event(
                    DockerOutputTests.Labels(target with { ProjectId = Guid.NewGuid() }, target.Containers[0]),
                    "start"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            finally
            {
                exited.TrySetResult();
            }
        }
    }

    /// <summary>An explicitly enabled smoke test verifies owned and unrelated Docker containers without host ports or volumes.</summary>
    [DockerSmokeFact]
    public async Task Real_docker_lifecycle_and_streams_use_only_disposable_owned_containers()
    {
        using var client = new DockerClientConfiguration(new Uri(OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock")).CreateClient();
        var name = "automate-diagnostics-test-" + Guid.NewGuid().ToString("N");
        var target = DockerOutputTests.Target() with { Containers = [new DockerContainerTarget(name, "web")] };
        var publisher = new RecordingPublisher();
        var collector = Collector(client, publisher);
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var daemon = collector.MonitorDaemonAsync(target, () => { }, token.Token);
        var created = new List<string>();
        Task? logs = null;
        try
        {
            foreach (var owned in new[] { false, true })
            {
                var labels = owned
                    ? DockerOutputTests.Labels(target, target.Containers[0])
                    : new Dictionary<string, string>();
                var result = await client.Containers.CreateContainerAsync(new CreateContainerParameters
                {
                    Name = owned ? name : name + "-unrelated",
                    Image = Environment.GetEnvironmentVariable("AUTOMATE_DOCKER_SMOKE_IMAGE") ?? "alpine:3.20",
                    Cmd = ["sh", "-c", "printf 'stdout 😀\\n'; printf 'password=private-value\\n' >&2"],
                    Labels = labels
                }, token.Token);
                created.Add(result.ID);
                await client.Containers.StartContainerAsync(result.ID, new ContainerStartParameters(), token.Token);
            }

            logs = collector.MonitorContainerAsync(target, target.Containers[0], token.Token);
            await publisher.Ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await publisher.Died.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains(publisher.Events, e => e.Message == "stdout 😀\n");
            Assert.Contains(publisher.Events,
                e => e.Message == "password=[REDACTED]\n" &&
                     e.SourceIdentity!.Stream == DeploymentDiagnosticStream.StandardError);
            Assert.DoesNotContain(publisher.Events, e => e.SourceIdentity?.InstanceId == created[0]);
            Assert.Contains(publisher.Events,
                e => e.Source == DeploymentDiagnosticSource.DockerDaemon && e.Attributes!["action"] == "create");
        }
        finally
        {
            token.Cancel();
            await AwaitShutdownAsync(daemon);
            if (logs is not null) await AwaitShutdownAsync(logs);
            foreach (var id in created)
                await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true },
                    CancellationToken.None);
        }
    }

    /// <summary>Real Compose receives deployment labels and reports exit outcomes without changing host resources.</summary>
    [DockerSmokeFact]
    public async Task Real_compose_injects_deployment_labels_and_reports_success_and_failure()
    {
        var target = DockerOutputTests.Target();
        var name = "automate-compose-test-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), name);
        var file = Path.Combine(directory, "docker-compose.yml");
        Directory.CreateDirectory(directory);
        using var client = new DockerClientConfiguration(new Uri(OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock")).CreateClient();
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var publisher = new RecordingPublisher();
        var cli = new DockerCli(new DockerOptions(), publisher, NullLogger.Instance, 60, new UnusedLive(),
            new DiagnosticRedactor(), new DeploymentRuntimeViewers(TimeProvider.System), TimeProvider.System);
        using var subscriptions = new CancellationTokenSource();
        var collector = Collector(client, publisher);
        var sources = new List<Task>();
        var image = Environment.GetEnvironmentVariable("AUTOMATE_DOCKER_SMOKE_IMAGE") ?? "alpine:3.20";
        await File.WriteAllTextAsync(file, $$"""
                                             services:
                                               web:
                                                 image: "{{image}}"
                                                 container_name: "{{name}}"
                                                 command: ["sh", "-c", "printf 'password=private-value\\n'; sleep 600"]
                                                 labels:
                                                   io.automate.owner: "AutoMate"
                                                   io.automate.project: "{{target.ProjectId:N}}"
                                                   io.automate.deployment: "${AUTOMATE_DEPLOYMENT_ID:-unassigned}"
                                                   io.automate.service: "web"
                                                   io.automate.scope: "deployment"
                                             """, token.Token);
        try
        {
            Assert.True(await cli.RunComposeAsync(directory, name, target.ProjectId, target.DeploymentId, token.Token,
                "up", "--no-build", "-d"));
            var inspect = await client.Containers.InspectContainerAsync(name, token.Token);
            Assert.Equal(target.DeploymentId.ToString("N"), inspect.Config.Labels["io.automate.deployment"]);
            target = target with { ComposeProject = name, Containers = [new DockerContainerTarget(name, "web")] };
            sources.Add(collector.MonitorDaemonAsync(target, () => { }, subscriptions.Token));
            sources.Add(collector.MonitorContainerAsync(target, target.Containers[0], subscriptions.Token));
            sources.Add(cli.StreamContainerMetricsAsync(inspect.ID, target.ProjectId, target.DeploymentId, "web",
                subscriptions.Token));
            Assert.False(await cli.RunComposeAsync(directory, name, target.ProjectId, target.DeploymentId, token.Token,
                "up", "--invalid-smoke-option"));
            Assert.Contains(publisher.Events,
                e => e.Attributes?.GetValueOrDefault("state") == "completed" && e.Attributes["exit_code"] == "0");
            Assert.Contains(publisher.Events,
                e => e.Attributes?.GetValueOrDefault("state") == "failed" &&
                     e.Severity == DeploymentDiagnosticSeverity.Error);
            Assert.All(publisher.Events, e => Assert.Equal(target.DeploymentId, e.DeploymentId));
            Assert.Contains(publisher.Events,
                e => e.SourceIdentity?.Stream == DeploymentDiagnosticStream.StandardError);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.True(await cli.RunComposeAsync(directory, name, target.ProjectId, target.DeploymentId, cleanup.Token,
                "down"));
            await subscriptions.CancelAsync();
            await Task.WhenAll(sources.Select(AwaitShutdownAsync)).WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(file);
            Directory.Delete(directory);
        }
    }

    /// <summary>Accepts source cancellation while still observing every task fault.</summary>
    private static async Task AwaitShutdownAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Creates a collector with the production redactor and deterministic fake transport.</summary>
    private static DockerDiagnosticCollector Collector(IDockerClient client, RecordingPublisher publisher)
    {
        return new DockerDiagnosticCollector(client, publisher, new DiagnosticRedactor(), TimeProvider.System,
            NullLogger.Instance);
    }

    /// <summary>Builds Docker's eight-byte multiplexed frame headers.</summary>
    private static byte[] Frames(params (int Stream, string Text)[] frames)
    {
        using var bytes = new MemoryStream();
        foreach (var frame in frames)
        {
            var text = Encoding.UTF8.GetBytes(frame.Text);
            bytes.Write(new byte[]
            {
                (byte)frame.Stream, 0, 0, 0, (byte)(text.Length >> 24), (byte)(text.Length >> 16),
                (byte)(text.Length >> 8), (byte)text.Length
            });
            bytes.Write(text);
        }

        return bytes.ToArray();
    }

    /// <summary>Creates a small SDK contract fake without adding a mocking dependency.</summary>
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, SdkProxy>();
        ((SdkProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>Counts warnings and rejects any raw provider error body.</summary>
    private sealed class CollectorLogger : ILogger
    {
        /// <summary>Number of actual failure warnings.</summary>
        public int Warnings { get; private set; }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        /// <inheritdoc />
        public void Log<TState>(LogLevel level, EventId id,
            TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(error);
            Assert.DoesNotContain("private", formatter(state, error));
            if (level == LogLevel.Warning) Warnings++;
        }
    }

    /// <summary>Dispatches only the SDK members a test expects.</summary>
    public class SdkProxy : DispatchProxy
    {
        /// <summary>Test-specific SDK response handler.</summary>
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod!, args);
        }
    }

    /// <summary>Fails if a Compose operation unexpectedly attempts direct live metric/terminal transport.</summary>
    private sealed class UnusedLive : ILogStreamer
    {
        /// <inheritdoc />
        public Task StreamContainerMetricsAsync(Guid projectId, string container, string cpu, string memory,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task StreamTerminalLogAsync(DeploymentTerminalLog diagnostic,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task StreamTerminalNoticeAsync(Guid projectId, string notice,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Records already-redacted durable admissions and safe source notices.</summary>
    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher, IDurableDeploymentDiagnosticPublisher
    {
        /// <summary>Simulates a failed durable receipt before any checkpoint is committed.</summary>
        public bool RejectNextLog { get; set; }

        /// <summary>Thread-safe captured records.</summary>
        public ConcurrentQueue<DeploymentDiagnosticEvent> Events { get; } = new();

        /// <summary>Container EOF notification.</summary>
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>First owned daemon lifecycle event.</summary>
        public TaskCompletionSource Lifecycle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Owned daemon exit event.</summary>
        public TaskCompletionSource Died { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public ValueTask PublishAsync(DeploymentDiagnosticEvent e, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(e);
            if (e.Source == DeploymentDiagnosticSource.DockerContainer && e.Kind == DeploymentDiagnosticKind.Lifecycle)
                Ended.TrySetResult();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> PublishDurablyAsync(DeploymentDiagnosticEvent e,
            CancellationToken cancellationToken = default)
        {
            if (RejectNextLog && e.Kind == DeploymentDiagnosticKind.Log)
            {
                RejectNextLog = false;
                return Task.FromResult(false);
            }

            Events.Enqueue(e);
            if (e.Source == DeploymentDiagnosticSource.DockerDaemon && e.Kind == DeploymentDiagnosticKind.Lifecycle)
                Lifecycle.TrySetResult();
            if (e.Attributes?.GetValueOrDefault("action") == "die") Died.TrySetResult();
            return Task.FromResult(true);
        }
    }

    /// <summary>Requires explicit opt-in and a pre-existing test image for disposable real-daemon verification.</summary>
    private sealed class DockerSmokeFactAttribute : FactAttribute
    {
        /// <summary>Skips real daemon access during normal credential-free test runs.</summary>
        public DockerSmokeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AUTOMATE_DOCKER_SMOKE") != "1")
                Skip = "Set AUTOMATE_DOCKER_SMOKE=1 to test a disposable local Docker container.";
        }
    }
}