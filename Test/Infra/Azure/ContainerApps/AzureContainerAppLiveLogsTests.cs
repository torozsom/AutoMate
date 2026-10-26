using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Infrastructure.Azure.ContainerApps;
using Infrastructure.Azure.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Test.Infra.TestSupport;

namespace Test.Infra.Azure.ContainerApps;

/// <summary>
///     Exercises incremental ACA HTTP reading, ownership boundaries and stream supervision without
///     Azure credentials.
/// </summary>
/// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
public sealed class AzureContainerAppLiveLogsTests
{
    /// <summary>
    ///     A record is delivered before the server ends its response, and cancellation closes a quiet
    ///     stream.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Console_is_delivered_before_eof_and_quiet_reader_cancels()
    {
        var pipe = new Pipe();
        var received = new TaskCompletionSource<AzureLiveLogRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var collector = Create(request =>
        {
            Assert.Equal("aca-token", request.Headers.Authorization!.Parameter);
            Assert.Contains("follow=true", request.RequestUri!.Query);
            Assert.Contains("tailLines=100", request.RequestUri.Query);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reading = collector.ReadAsync(ConsoleEndpoint(), "aca-token", record =>
        {
            received.TrySetResult(record);

            return Task.CompletedTask;
        }, () =>
        {
            connected.TrySetResult();

            return Task.CompletedTask;
        }, cancellation.Token);

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Force split JSON/UTF-8 boundaries and include an invalid line before a valid stderr envelope.
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("bad json\n{\"TimeStamp\":\"2026-10-11T12:00:00Z\","));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("\"Log\":\"árvíz\",\"Stream\":\"stderr\"}\n"));
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("árvíz", actual.Message);
        Assert.Equal(DeploymentDiagnosticStream.StandardError, actual.Stream);
        Assert.False(reading.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await pipe.Writer.CompleteAsync();
    }

    /// <summary>
    ///     Oversized records do not allocate unbounded memory or suppress the next valid record.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Oversized_and_malformed_envelopes_do_not_suppress_following_records()
    {
        var text = new string('x', 70000) + "\n" +
                   "{\"TimeStamp\":\"2026-10-11T12:00:00Z\",\"Log\":\"next\"}";
        var collector = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        var records = new List<AzureLiveLogRecord>();

        await collector.ReadAsync(ConsoleEndpoint(), "token", record =>
        {
            records.Add(record);

            return Task.CompletedTask;
        }, () => Task.CompletedTask, CancellationToken.None);

        Assert.Equal("next", Assert.Single(records).Message);
    }

    /// <summary>
    ///     Replica enumeration retains every container and constructs missing stream URLs from the
    ///     verified app endpoint.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Discovery_selects_exact_revision_and_enumerates_all_replica_containers()
    {
        var collector = Create(request =>
        {
            Assert.Equal("arm-token", request.Headers.Authorization!.Parameter);

            if (request.RequestUri!.AbsolutePath.EndsWith("/replicas"))
            {
                Assert.Contains("/revisions/rev-1/replicas", request.RequestUri.AbsolutePath);

                return DelegateHttpMessageHandler.Json("""
                                                       {"value":[{"name":"pod-1","properties":{"containers":[{"name":"web"},{"name":"db"}]}},
                                                                 {"name":"pod-2","properties":{"containers":[{"name":"web"}]}}]}
                                                       """);
            }

            return AppResponse();
        });

        var endpoints = await collector.DiscoverAsync(Target(), CancellationToken.None);

        Assert.Equal(4, endpoints.Count);
        Assert.Single(endpoints, e => e.System);
        Assert.Contains(endpoints, e => e.Key == "pod-2/web" &&
                                        e.Uri.AbsolutePath.EndsWith(
                                            "/revisions/rev-1/replicas/pod-2/containers/web/logstream"));
    }

    /// <summary>
    ///     An untrusted discovery response is rejected before any ACA token can be sent to its origin.
    /// </summary>
    /// <param name="endpoint">Parsed HTTP(S) collector endpoint, or null when invalid or absent.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("https://attacker.example/logstream")]
    [InlineData("http://centralus.azurecontainerapps.dev/logstream")]
    [InlineData("https://user:secret@centralus.azurecontainerapps.dev/logstream")]
    [InlineData("https://centralus.azurecontainerapps.dev.attacker.example/logstream")]
    public async Task Discovery_rejects_untrusted_endpoints(string endpoint)
    {
        var requests = 0;
        var collector = Create(_ =>
        {
            requests++;

            return DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new
                { properties = new { eventStreamEndpoint = endpoint } }));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collector.DiscoverAsync(Target(), CancellationToken.None));

        Assert.Equal(1, requests);
    }

    /// <summary>
    ///     Platform messages retain severity/time and application JSON text does not rewrite envelope
    ///     identity.
    /// </summary>
    [Fact]
    public void Parser_and_routing_preserve_source_identity()
    {
        Assert.True(AzureContainerAppLiveLogs.TryParse("""
                                                       {"TimeStamp":"2026-10-11T14:00:00+02:00","Msg":"failed probe","RevisionName":"rev-1","Type":"Warning"}
                                                       """, true, out var record));
        Assert.Equal(DeploymentDiagnosticSeverity.Warning, record!.Severity);
        Assert.Equal(TimeSpan.Zero, record.Timestamp.Offset);

        var db = AzureContainerAppLiveLogs.CreateEvent(Target(), ConsoleEndpoint() with { Container = "db" }, record);

        Assert.Equal("azure-db", db.TerminalChannel.Target);
        Assert.Equal(DeploymentDiagnosticComponent.Database, db.SourceIdentity!.Component);
        Assert.Equal("pod-1", db.Attributes!["replica"]);

        var web = AzureContainerAppLiveLogs.CreateEvent(Target(), ConsoleEndpoint(), record);

        Assert.Equal("cloud-web", web.TerminalChannel.Target);
        Assert.Equal(web.EventId,
            AzureContainerAppLiveLogs.CreateEvent(Target(web.DeploymentId), ConsoleEndpoint(), record).EventId);
        Assert.NotEqual(web.EventId, AzureContainerAppLiveLogs.CreateEvent(Target(web.DeploymentId),
            ConsoleEndpoint() with { Key = "pod-2/web", Replica = "pod-2" }, record).EventId);
    }

    /// <summary>
    ///     Log Analytics is unnecessary for live collection; wrong revisions are rejected and lease
    ///     shutdown is prompt.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Supervisor_streams_without_monitor_token_and_filters_other_revisions()
    {
        var publisher = new RecordingPublisher();
        var target = Target();
        var authCalls = 0;

        var collector = Create(request =>
        {
            if (request.RequestUri!.Host != "management.azure.com")
            {
                Assert.Equal("aca-token", request.Headers.Authorization!.Parameter);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                                                {"TimeStamp":"2026-10-11T12:00:00Z","Log":"wrong","RevisionName":"rev-2"}
                                                {"TimeStamp":"2026-10-11T12:00:01Z","Log":"live","RevisionName":"rev-1","Stream":"stderr"}

                                                """)
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("getAuthtoken"))
            {
                Assert.Equal(HttpMethod.Post, request.Method);

                Interlocked.Increment(ref authCalls);

                return DelegateHttpMessageHandler.Json(
                    """{"properties":{"token":"aca-token","expires":"2099-01-01T00:00:00Z"}}""");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("replicas"))
                return DelegateHttpMessageHandler.Json(
                    """{"value":[{"name":"pod-1","properties":{"containers":[{"name":"web"}]}}]}""");

            return AppResponse();
        }, publisher);

        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        collector.Update(target, lifetime.Token);
        await publisher.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await collector.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, authCalls);
        Assert.DoesNotContain(publisher.Events, e => e.Message == "wrong");
        Assert.Contains(publisher.Events, e => e.Message == "live" && e.TerminalChannel.Target == "cloud-web" &&
                                               e.ProjectId == target.ProjectId &&
                                               e.DeploymentId == target.DeploymentId);
    }

    /// <summary>
    ///     Authorization failures give fixed actionable guidance, never provider response bodies or
    ///     credentials.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Access_denied_is_visible_in_system_terminal_without_provider_payload()
    {
        var publisher = new RecordingPublisher();
        var collector = Create(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("private-provider-secret") }, publisher);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        collector.Update(Target(), lifetime.Token);
        await publisher.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await collector.StopAsync();

        Assert.Contains(publisher.Events, e => e.Message.Contains("log-stream permission") &&
                                               e.TerminalChannel.Kind == DeploymentTerminalChannelKind.System);
        Assert.DoesNotContain(publisher.Events, e => e.Message.Contains("private-provider-secret"));
    }

    /// <summary>
    ///     Reconnect tails retain stable identities and only exact acknowledged console rows reconcile
    ///     with history.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Reconnect_deduplicates_confirmed_tail_and_reconciles_exact_history()
    {
        var publisher = new RecordingPublisher();
        var target = Target();
        var secondConsole = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consoleRequests = 0;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host != "management.azure.com")
            {
                if (request.RequestUri.AbsolutePath.EndsWith("logstream"))
                {
                    if (Interlocked.Increment(ref consoleRequests) >= 2) secondConsole.TrySetResult();

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                                                    {"TimeStamp":"2026-10-11T12:00:01Z","Log":"live","Stream":"stderr"}

                                                    """)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("getAuthtoken"))
                return DelegateHttpMessageHandler.Json(
                    """{"properties":{"token":"aca-token","expires":"2099-01-01T00:00:00Z"}}""");
            if (request.RequestUri.AbsolutePath.EndsWith("replicas"))
                return DelegateHttpMessageHandler.Json(
                    """{"value":[{"name":"pod-1","properties":{"containers":[{"name":"web"}]}}]}""");

            return AppResponse();
        });
        var collector = new AzureContainerAppLiveLogs(new StubHttpClientFactory(handler), publisher,
            NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        collector.Update(target, lifetime.Token);
        await secondConsole.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var history = new AzureMonitorLogRecord(DateTimeOffset.Parse("2026-10-11T12:00:01Z"), "live",
            "web", "rev-1", "stderr", "ContainerAppConsoleLogs", ReplicaName: "pod-1");

        Assert.True(collector.WasConfirmedLive(target.DeploymentId, history));
        Assert.False(collector.WasConfirmedLive(target.DeploymentId, history with { ReplicaName = "pod-2" }));
        Assert.False(collector.WasConfirmedLive(target.DeploymentId, history with { ReplicaName = "" }));
        Assert.False(collector.WasConfirmedLive(target.DeploymentId, history with { Stream = "stdout" }));

        await collector.StopAsync();

        Assert.Single(publisher.Events, e => e.Message == "live");
    }

    /// <summary>
    ///     A stale collection lease cannot establish a live Azure connection.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Expired_ownership_does_not_connect()
    {
        var requests = 0;
        var collector = Create(_ =>
        {
            requests++;

            return AppResponse();
        });

        collector.Update(Target() with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None);
        await collector.StopAsync();

        Assert.Equal(0, requests);
    }

    /// <summary>
    ///     Creates a credential-free collector with a controlled HTTP boundary.
    /// </summary>
    /// <param name="respond">Callback supplying a controlled synthetic provider or HTTP response.</param>
    /// <param name="publisher">Accepts normalized observations for bounded redaction and durable delivery.</param>
    /// <returns>The configured Azure container app live logs instance or response for this scenario.</returns>
    private static AzureContainerAppLiveLogs Create(Func<HttpRequestMessage, HttpResponseMessage> respond,
        RecordingPublisher? publisher = null)
    {
        return new AzureContainerAppLiveLogs(new StubHttpClientFactory(new DelegateHttpMessageHandler(respond)),
            publisher ?? new RecordingPublisher(), NullLogger.Instance);
    }

    /// <summary>
    ///     Returns the app's provider-derived stream endpoint.
    /// </summary>
    /// <returns>The configured HTTP response message instance or response for this scenario.</returns>
    private static HttpResponseMessage AppResponse()
    {
        return DelegateHttpMessageHandler.Json("""
                                               {"properties":{"eventStreamEndpoint":"https://centralus.azurecontainerapps.dev/subscriptions/sub/resourceGroups/rg/containerApps/app/eventstream"}}
                                               """);
    }

    /// <summary>
    ///     Uses stable ownership IDs so repeated envelopes have comparable retry identities.
    /// </summary>
    /// <param name="deployment">Synthetic deployment identity, or null when absent.</param>
    /// <returns>The configured Azure live log target instance or response for this scenario.</returns>
    private static AzureLiveLogTarget Target(Guid? deployment = null)
    {
        return new AzureLiveLogTarget(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            deployment ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/app", "rev-1", "arm-token",
            DateTimeOffset.UtcNow.AddMinutes(2), true, new HashSet<string> { "db" });
    }

    /// <summary>
    ///     Returns a trusted console address for incremental transport tests.
    /// </summary>
    /// <returns>The configured Azure live log endpoint instance or response for this scenario.</returns>
    private static AzureLiveLogEndpoint ConsoleEndpoint()
    {
        return new AzureLiveLogEndpoint("pod-1/web",
            new Uri("https://centralus.azurecontainerapps.dev/logstream"), false, "pod-1", "web");
    }

    /// <summary>
    ///     Captures durable records and queued status guidance across concurrent console/system
    ///     readers.
    /// </summary>
    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher, IDurableDeploymentDiagnosticPublisher
    {
        /// <summary>Normalized events observed at the publication boundary.</summary>
        public ConcurrentQueue<DeploymentDiagnosticEvent> Events { get; } = new();

        /// <summary>Signals a confirmed web console observation.</summary>
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals actionable provider guidance.</summary>
        public TaskCompletionSource Notice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Records normalized events and signals observed log-stream permission guidance.
        /// </summary>
        /// <param name="e">Browser change or keyboard event.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public ValueTask PublishAsync(DeploymentDiagnosticEvent e, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(e);
            if (e.Message.Contains("log-stream permission")) Notice.TrySetResult();

            return ValueTask.CompletedTask;
        }

        /// <summary>
        ///     Records the event, signals cloud-web delivery and acknowledges the synthetic durable write.
        /// </summary>
        /// <param name="e">Browser change or keyboard event.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning true for every fixture call.</returns>
        public Task<bool> PublishDurablyAsync(DeploymentDiagnosticEvent e, CancellationToken cancellationToken)
        {
            Events.Enqueue(e);
            if (e.TerminalChannel.Target == "cloud-web") Delivered.TrySetResult();

            return Task.FromResult(true);
        }
    }
}
