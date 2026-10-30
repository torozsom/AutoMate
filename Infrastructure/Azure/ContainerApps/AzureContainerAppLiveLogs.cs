using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Infrastructure.Azure.Monitor;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Azure.ContainerApps;

/// <summary>
///     Supervises ACA console/event HTTP streams independently of historical queries and metric
///     polling.
/// </summary>
/// <param name="clients">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="publisher">Accepts normalized observations for redaction and durable delivery.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="discoveryInterval">Bounded interval between verified replica discovery attempts.</param>
internal sealed class AzureContainerAppLiveLogs(
    IHttpClientFactory clients,
    IDeploymentDiagnosticPublisher publisher,
    ILogger logger,
    TimeSpan? discoveryInterval = null)
{
    /// <summary>Stable ACA version shared with the existing resource-state adapter.</summary>
    private const string ApiVersion = "2024-03-01";

    /// <summary>
    ///     Replica reconciliation cadence; tests can accelerate it without altering HTTP or
    ///     cancellation semantics.
    /// </summary>
    private readonly TimeSpan _discoveryInterval = discoveryInterval ?? TimeSpan.FromSeconds(15);

    /// <summary>Cancelled supervisors awaiting deterministic shutdown.</summary>
    private readonly ConcurrentBag<Task> _retired = [];

    /// <summary>One supervised stream inventory per registered deployment.</summary>
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    /// <summary>
    ///     Renews the polling owner's permission to collect; a stalled or lost lease expires live
    ///     connections.
    /// </summary>
    /// <param name="target">Verified Azure app, revision and collector lifetime metadata.</param>
    /// <param name="hostToken">Signals host shutdown.</param>
    internal void Update(AzureLiveLogTarget target, CancellationToken hostToken)
    {
        while (_retired.TryTake(out var retired))
        {
            if (retired.IsCompletedSuccessfully) continue;
            _retired.Add(retired);
            break;
        }

        if (_sessions.TryGetValue(target.DeploymentId, out var existing))
        {
            if (existing.Target.Revision == target.Revision && !existing.Task.IsCompleted)
            {
                existing.Target = target;
                return;
            }

            Remove(target.DeploymentId);
        }

        var session = new Session(target, hostToken);
        if (!_sessions.TryAdd(target.DeploymentId, session))
        {
            session.Cancellation.Dispose();
            return;
        }

        session.Task = RunAsync(session);
    }

    /// <summary>
    ///     Cancels collection immediately when its project/deployment is retired or another host
    ///     owns the lease.
    /// </summary>
    /// <param name="deployment">The exact deployment identity or tracked metadata for this operation.</param>
    internal void Remove(Guid deployment)
    {
        if (!_sessions.TryRemove(deployment, out var session)) return;
        session.Cancellation.Cancel();
        _retired.Add(RetireAsync(session));
    }

    /// <summary>
    ///     Disposes a session only after its supervisor and all readers have observed cancellation.
    /// </summary>
    /// <param name="session">Supervised collector session and its owned lifetime.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private static async Task RetireAsync(Session session)
    {
        await session.Task;
        session.Cancellation.Dispose();
    }

    /// <summary>
    ///     Observes every connection task before the hosted coordinator finishes shutdown.
    /// </summary>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal async Task StopAsync()
    {
        foreach (var id in _sessions.Keys) Remove(id);
        await Task.WhenAll(_retired);
    }

    /// <summary>
    ///     Historical rows with exact replica/time/text identity can reuse a recent durable live
    ///     acknowledgement.
    /// </summary>
    /// <param name="deployment">The exact deployment identity or tracked metadata for this operation.</param>
    /// <param name="record">Normalized Azure event with its original timestamp and ingestion cursor.</param>
    /// <returns>Whether the documented was confirmed live condition holds.</returns>
    internal bool WasConfirmedLive(Guid deployment, AzureMonitorLogRecord record)
    {
        return record.ReplicaName.Length > 0 && _sessions.TryGetValue(deployment, out var session) &&
               session.ConfirmedConsole.ContainsKey(ConsoleFingerprint(record.TimestampUtc, record.Message,
                   record.RevisionName, record.ContainerName, record.ReplicaName, record.Stream));
    }

    /// <summary>
    ///     Does not infer equivalence when Azure changes timestamps or omits the replica identity.
    /// </summary>
    /// <param name="time">The observation timestamp to assign to a UTC bucket.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="revision">Exact verified Azure revision identity.</param>
    /// <param name="container">Verified owned container identity used for observation routing.</param>
    /// <param name="replica">Verified Container App replica identity.</param>
    /// <param name="stream">The typed diagnostic output stream.</param>
    /// <returns>The stable identity derived from the documented metadata or content.</returns>
    private static string ConsoleFingerprint(DateTimeOffset time, string message, string revision,
        string container, string replica, string stream)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{time:O}\n{revision}\n{container}\n{replica}\n{stream}\n{message}")));
    }

    /// <summary>
    ///     Reconciles replica changes and independently retries completed/failed streams every
    ///     fifteen seconds.
    /// </summary>
    /// <param name="session">Supervised collector session and its owned lifetime.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task RunAsync(Session session)
    {
        var connections = new Dictionary<string, Connection>(StringComparer.Ordinal);
        var token = session.Cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested && session.Target.LeaseUntil > DateTimeOffset.UtcNow)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var target = session.Target;
                    var endpoints = await DiscoverAsync(target, deadline.Token);
                    foreach (var key in connections.Keys
                                 .Except(endpoints
                                     .Select(e => e.Key))
                                 .ToArray())
                    {
                        await connections[key].Cancellation.CancelAsync();
                        await connections[key].Task;
                        connections[key].Cancellation.Dispose();
                        connections.Remove(key);
                        session.Issues.TryRemove(key, out _);
                        session.Issues.TryRemove(key + "-connected", out _);
                    }

                    foreach (var endpoint in endpoints)
                    {
                        if (connections.TryGetValue(endpoint.Key, out var connection))
                        {
                            if (!connection.Task.IsCompleted) continue;
                            connection.Cancellation.Dispose();
                        }

                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        connections[endpoint.Key] = new Connection(cancellation,
                            FollowAsync(session, endpoint, cancellation.Token));
                    }

                    if (endpoints.All(e => e.System))
                        await NoticeAsync(session, "replicas",
                            "ACA has no running replica for this deployment. Console streaming will resume when a replica starts.",
                            token);
                    else session.Issues.TryRemove("replicas", out _);
                    session.Issues.TryRemove("discovery", out _);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("ACA live discovery failed for deployment {DeploymentId}. Failure {FailureType}.",
                        session.Target.DeploymentId, ex.GetType().Name);
                    await NoticeAsync(session, "discovery", FailureMessage(ex), token);
                }

                var remaining = session.Target.LeaseUntil - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < _discoveryInterval ? remaining : _discoveryInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var connection in connections.Values) await connection.Cancellation.CancelAsync();
            await Task.WhenAll(connections.Values.Select(c => c.Task));
            foreach (var connection in connections.Values) connection.Cancellation.Dispose();
        }
    }

    /// <summary>
    ///     Enumerates only the verified revision and uses Azure's event endpoint as the trusted
    ///     stream origin.
    /// </summary>
    /// <param name="target">Verified Azure app, revision and collector lifetime metadata.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the discover async selection with its documented bounds and ordering.</returns>
    /// <exception cref="InvalidOperationException">ACA did not return a trusted stream endpoint.</exception>
    internal async Task<IReadOnlyList<AzureLiveLogEndpoint>> DiscoverAsync(AzureLiveLogTarget target,
        CancellationToken cancellationToken)
    {
        using var app = await ArmAsync(target.ResourceId, target.AccessToken, HttpMethod.Get, cancellationToken);

        var properties = app.RootElement.GetProperty("properties");
        var eventUrl = String(properties, "eventStreamEndpoint");

        if (!Uri.TryCreate(eventUrl, UriKind.Absolute, out var system) || !TrustedEndpoint(system))
            throw new InvalidOperationException("ACA did not return a trusted stream endpoint.");

        var endpoints = new List<AzureLiveLogEndpoint> { new("system", system, true, "", "") };
        var path = target.ResourceId + "/revisions/" + Uri.EscapeDataString(target.Revision) + "/replicas";

        using var replicas = await ArmAsync(path, target.AccessToken, HttpMethod.Get, cancellationToken);

        if (replicas.RootElement.TryGetProperty("nextLink", out var next) && !string.IsNullOrEmpty(next.GetString()))
            throw new InvalidOperationException("ACA replica inventory exceeds the bounded stream inventory.");

        foreach (var replica in replicas.RootElement.GetProperty("value").EnumerateArray())
        {
            var replicaName = String(replica, "name");
            if (string.IsNullOrEmpty(replicaName)) continue;

            var replicaProperties = replica.GetProperty("properties");
            if (!replicaProperties.TryGetProperty("containers", out var containers)) continue;

            foreach (var container in containers.EnumerateArray())
            {
                var name = String(container, "name");
                if (string.IsNullOrEmpty(name)) continue;
                var endpointText = String(container, "logStreamEndpoint");

                Uri endpoint;
                if (Uri.TryCreate(endpointText, UriKind.Absolute, out var discovered))
                {
                    endpoint = discovered;
                }
                else
                {
                    var appPath = system.AbsolutePath;
                    var marker = appPath.LastIndexOf("/eventstream", StringComparison.OrdinalIgnoreCase);
                    if (marker < 0) throw new InvalidOperationException("ACA event endpoint has an unsupported path.");
                    endpoint = new Uri(system.GetLeftPart(UriPartial.Authority) + appPath[..marker] +
                                       $"/revisions/{Uri.EscapeDataString(target.Revision)}" +
                                       $"/replicas/{Uri.EscapeDataString(replicaName)}" +
                                       $"/containers/{Uri.EscapeDataString(name)}/logstream");
                }

                if (!TrustedEndpoint(endpoint) || endpoint.Authority != system.Authority)
                    throw new InvalidOperationException(
                        "ACA container endpoint does not match the verified stream origin.");

                if (endpoints.Count >= 65)
                    throw new InvalidOperationException("ACA live collection is limited to 64 containers.");
                endpoints.Add(new AzureLiveLogEndpoint(replicaName + "/" + name, endpoint, false, replicaName, name));
            }
        }

        return endpoints;
    }

    /// <summary>
    ///     Refreshes ACA credentials for each reconnect and never blocks polling on the streaming
    ///     response body.
    /// </summary>
    /// <param name="session">Supervised collector session and its owned lifetime.</param>
    /// <param name="endpoint">Verified provider endpoint selected for this operation.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task FollowAsync(Session session, AzureLiveLogEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var target = session.Target;
            using var auth = await ArmAsync(target.ResourceId + "/getAuthtoken", target.AccessToken,
                HttpMethod.Post, cancellationToken);

            var properties = auth.RootElement.GetProperty("properties");
            var token = String(properties, "token");

            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("ACA stream token is unavailable.");
            var expires = DateTimeOffset.TryParse(String(properties, "expires"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var expiry)
                ? expiry.AddSeconds(-10)
                : DateTimeOffset.UtcNow.AddMinutes(5);

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var duration = expires - DateTimeOffset.UtcNow;

            if (duration <= TimeSpan.Zero) throw new InvalidOperationException("ACA returned an expired stream token.");

            // Reconnect periodically to recheck credentials and collection ownership even on a quiet stream.
            lifetime.CancelAfter(duration < TimeSpan.FromSeconds(90) ? duration : TimeSpan.FromSeconds(90));
            await ReadAsync(endpoint, token, async record =>
            {
                if (session.Target.LeaseUntil <= DateTimeOffset.UtcNow)
                    throw new OperationCanceledException(lifetime.Token);

                if (record.Revision.Length > 0 && record.Revision != target.Revision) return;
                if (endpoint.System && record.Revision.Length == 0 && !session.Target.IncludeAppEvents) return;

                var diagnostic = CreateEvent(target, endpoint, record);
                var identity = diagnostic.EventId!.Value;

                if (session.Delivered.ContainsKey(identity)) return;
                if (publisher is IDurableDeploymentDiagnosticPublisher durable &&
                    await durable.PublishDurablyAsync(diagnostic, lifetime.Token))
                {
                    session.Delivered.TryAdd(identity, 0);
                    var fingerprint = endpoint.System
                        ? null
                        : ConsoleFingerprint(record.Timestamp, record.Message,
                            target.Revision, endpoint.Container, endpoint.Replica,
                            record.Stream == DeploymentDiagnosticStream.StandardError ? "stderr" : "stdout");

                    if (fingerprint is not null) session.ConfirmedConsole.TryAdd(fingerprint, 0);
                    session.Order.Enqueue((identity, fingerprint));
                    while (session.Order.Count > 4096 && session.Order.TryDequeue(out var old))
                    {
                        session.Delivered.TryRemove(old.Id, out _);
                        if (old.Fingerprint is not null) session.ConfirmedConsole.TryRemove(old.Fingerprint, out _);
                    }
                }
                else
                {
                    await publisher.PublishAsync(diagnostic, lifetime.Token);
                }
            }, async () =>
            {
                session.Issues.TryRemove(endpoint.Key, out _);
                await NoticeAsync(session, endpoint.Key + "-connected", endpoint.System
                        ? "ACA live system log stream connected."
                        : $"ACA live console connected: {endpoint.Container} / {endpoint.Replica}.", lifetime.Token,
                    DeploymentDiagnosticSeverity.Information);
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            await NoticeAsync(session, endpoint.Key,
                "ACA live log connection timed out; AutoMate will reconnect automatically.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            /* Token/ownership refresh reconnects on the next discovery tick. */
        }
        catch (Exception ex)
        {
            logger.LogWarning("ACA live stream failed for deployment {DeploymentId}. Failure {FailureType}.",
                session.Target.DeploymentId, ex.GetType().Name);
            await NoticeAsync(session, endpoint.Key, FailureMessage(ex), cancellationToken);
        }
    }

    /// <summary>
    ///     Reads bounded newline JSON records as they arrive; console payload text is not
    ///     interpreted as platform metadata.
    /// </summary>
    /// <param name="endpoint">Verified provider endpoint selected for this operation.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="receive">Callback receiving bounded normalized live records.</param>
    /// <param name="connected">Callback invoked after the live stream connects.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal async Task ReadAsync(AzureLiveLogEndpoint endpoint, string accessToken,
        Func<AzureLiveLogRecord, Task> receive, Func<Task> connected, CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(endpoint.Uri)
        {
            Query = "follow=true&output=json&tailLines=100"
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var client = clients.CreateClient("AzureLiveLogs");
        using var response =
            await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        await connected();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var buffer = new char[4096];
        var line = new StringBuilder();
        var overflow = false;
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            for (var i = 0; i < read; i++)
                if (buffer[i] == '\n')
                {
                    if (!overflow && TryParse(line.ToString(), endpoint.System, out var record)) await receive(record!);
                    line.Clear();
                    overflow = false;
                }
                else if (line.Length < 65536)
                {
                    line.Append(buffer[i]);
                }
                else
                {
                    overflow = true;
                }

        if (!overflow && TryParse(line.ToString(), endpoint.System, out var last)) await receive(last!);
    }

    /// <summary>
    ///     Accepts the ACA JSON envelope while retaining provider time, stderr and platform
    ///     severity.
    /// </summary>
    /// <param name="line">One bounded source-output line to normalize and redact.</param>
    /// <param name="system">Whether this endpoint or record represents platform/system output.</param>
    /// <param name="record">Provider observation or retained diagnostic record to process.</param>
    /// <returns>True when validation or acquisition succeeds; otherwise false.</returns>
    internal static bool TryParse(string line, bool system, out AzureLiveLogRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var message = String(root, "Log");

            if (message.Length == 0 && system) message = String(root, "Msg");
            if (message.Length == 0) message = String(root, "Message");
            if (message.Length == 0) return false;

            var timestamp = String(root, "TimeStamp");
            if (timestamp.Length == 0) timestamp = String(root, "Timestamp");
            if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out var time)) return false;

            var stream = system
                ? DeploymentDiagnosticStream.System
                : String(root, "Stream").Equals("stderr", StringComparison.OrdinalIgnoreCase)
                    ? DeploymentDiagnosticStream.StandardError
                    : DeploymentDiagnosticStream.StandardOutput;

            var severity = String(root, "Type").ToLowerInvariant() switch
            {
                "error" => DeploymentDiagnosticSeverity.Error,
                "warning" => DeploymentDiagnosticSeverity.Warning,
                _ => DeploymentDiagnosticSeverity.Information
            };

            record = new AzureLiveLogRecord(time.ToUniversalTime(), message, String(root, "RevisionName"), stream,
                severity);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Preserves replica identity for retry deduplication and routes known database containers
    ///     explicitly.
    /// </summary>
    /// <param name="target">Verified Azure app, revision and collector lifetime metadata.</param>
    /// <param name="endpoint">Verified provider endpoint selected for this operation.</param>
    /// <param name="record">Provider observation or retained diagnostic record to process.</param>
    /// <returns>The normalized deployment-correlated observation.</returns>
    internal static DeploymentDiagnosticEvent CreateEvent(AzureLiveLogTarget target, AzureLiveLogEndpoint endpoint,
        AzureLiveLogRecord record)
    {
        var database = target.DatabaseContainers.Contains(endpoint.Container);
        var fingerprint =
            $"{target.DeploymentId:N}/live/{endpoint.Key}/{record.Timestamp:O}/{record.Stream}/{record.Message}";

        return new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
            DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Log, record.Severity,
            record.Timestamp, record.Message,
            endpoint.System
                ? new DeploymentTerminalChannel(DeploymentTerminalChannelKind.System)
                : new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container,
                    database
                        ? "azure-db"
                        : "cloud-web"),
            new Dictionary<string, string>
            {
                ["transport"] = "aca-live",
                ["revision"] = record.Revision.Length > 0 ? record.Revision : target.Revision,
                ["replica"] = endpoint.Replica,
                ["container"] = endpoint.Container
            }, SourceIdentity: new DeploymentDiagnosticSourceIdentity(endpoint.System
                    ? DeploymentDiagnosticComponent.Revision
                    : database
                        ? DeploymentDiagnosticComponent.Database
                        : DeploymentDiagnosticComponent.Container,
                record.Stream, endpoint.Key),
            EventId: new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))[..16]));
    }

    /// <summary>
    ///     Uses ARM credentials only on the fixed management origin and disposes responses before
    ///     returning metadata.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <param name="method">HTTP method used for the bounded provider request.</param>
    /// <param name="ct">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the owned parsed JSON response; the caller must dispose it.</returns>
    private async Task<JsonDocument> ArmAsync(string path, string token, HttpMethod method, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method,
            AzureConstants.ManagementEndpoint + path + "?api-version=" + ApiVersion);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var client = clients.CreateClient("AzureRuntime");
        using var response = await client.SendAsync(request, ct);

        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    /// <summary>
    ///     Publishes bounded per-source status guidance through redaction into the Azure System
    ///     terminal.
    /// </summary>
    /// <param name="session">Supervised collector session and its owned lifetime.</param>
    /// <param name="key">Stable lookup or configuration key.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="ct">Cancels the underlying operation when observed.</param>
    /// <param name="severity">The normalized severity used to rank a context group.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task NoticeAsync(Session session, string key, string message, CancellationToken ct,
        DeploymentDiagnosticSeverity severity = DeploymentDiagnosticSeverity.Warning)
    {
        if (!session.Issues.TryAdd(key, 0) || ct.IsCancellationRequested) return;
        var target = session.Target;
        try
        {
            await publisher.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Lifecycle, severity,
                DateTimeOffset.UtcNow, message, new DeploymentTerminalChannel(DeploymentTerminalChannelKind.System),
                SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Revision,
                    DeploymentDiagnosticStream.Control, target.Revision)), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            session.Issues.TryRemove(key, out _);
            logger.LogWarning("ACA live notice delivery failed for deployment {DeploymentId}. Failure {FailureType}.",
                target.DeploymentId, ex.GetType().Name);
        }
    }

    /// <summary>
    ///     Translates failures without exposing request URLs, bearer tokens or provider response
    ///     bodies.
    /// </summary>
    /// <param name="error">Original failure to classify without persisting arbitrary exception text.</param>
    /// <returns>The failure message derived from the validated input.</returns>
    private static string FailureMessage(Exception error)
    {
        return error is HttpRequestException
        {
            StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
        }
            ? "ACA live log access was denied. The connected Azure account needs Container Apps log-stream permission; reconnect Azure after updating access."
            : "ACA live logs are temporarily unavailable; AutoMate will reconnect automatically. Historical collection continues.";
    }

    /// <summary>
    ///     Restricts bearer forwarding to public ACA development endpoints with TLS and no embedded
    ///     credentials.
    /// </summary>
    /// <param name="uri">Validated provider endpoint URI.</param>
    /// <returns>Whether the documented trusted endpoint condition holds.</returns>
    private static bool TrustedEndpoint(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttps
               && uri.Port == 443
               && uri.Host.EndsWith(".azurecontainerapps.dev", StringComparison.OrdinalIgnoreCase)
               && uri.UserInfo.Length == 0
               && uri.Query.Length == 0
               && uri.Fragment.Length == 0;
    }

    /// <summary>
    ///     Reads only string-valued envelope metadata, leaving container payloads opaque.
    /// </summary>
    /// <param name="element">
    ///     JSON value to validate or normalize without retaining arbitrary provider prose.
    /// </param>
    /// <param name="name">The recorded display name or fixed metric identity.</param>
    /// <returns>The string derived from the validated input.</returns>
    private static string String(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    /// <summary>
    ///     Owns one deployment's lifetime, latest credentials and bounded confirmed-delivery cache.
    /// </summary>
    /// <param name="target">Verified Azure app, revision and collector lifetime metadata.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    private sealed class Session(AzureLiveLogTarget target, CancellationToken token)
    {
        /// <summary>Immutable ownership/credential snapshot atomically renewed by the polling coordinator.</summary>
        public volatile AzureLiveLogTarget Target = target;

        /// <summary>Cancellation shared by discovery and every active reader.</summary>
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);

        /// <summary>Observed supervisor completion used for retirement and shutdown.</summary>
        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>Per-source notices suppressed until the corresponding recovery or replica removal.</summary>
        public ConcurrentDictionary<string, byte> Issues { get; } = new();

        /// <summary>Recent confirmed event identities suppress reconnect tail duplication.</summary>
        public ConcurrentDictionary<Guid, byte> Delivered { get; } = new();

        /// <summary>
        ///     Exact console identities shared with delayed historical reads after durable live
        ///     acknowledgement.
        /// </summary>
        public ConcurrentDictionary<string, byte> ConfirmedConsole { get; } = new();

        /// <summary>Insertion order bounds the retry cache to 4,096 records.</summary>
        public ConcurrentQueue<(Guid Id, string? Fingerprint)> Order { get; } = new();
    }

    /// <summary>
    ///     One independently cancellable HTTP reader in the currently verified inventory.
    /// </summary>
    /// <param name="Cancellation">
    ///     Owns cancellation of the supervised work and must be disposed by its owner.
    /// </param>
    /// <param name="Task">Owned operation to await while preserving its completion or failure.</param>
    private sealed record Connection(CancellationTokenSource Cancellation, Task Task);
}

/// <summary>
///     Immutable credentials and ownership scope refreshed only after a successful coordinator
///     lease claim.
/// </summary>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="DeploymentId">
///     The deployment identity; null is used only where legacy observations permit it.
/// </param>
/// <param name="ResourceId">Verified Azure resource identity used to scope provider reads.</param>
/// <param name="Revision">Exact verified Azure revision identity.</param>
/// <param name="AccessToken">The provider access token; never include it in diagnostics.</param>
/// <param name="LeaseUntil">The UTC expiry of the current lease.</param>
/// <param name="IncludeAppEvents">Whether the query includes Container App event observations.</param>
/// <param name="DatabaseContainers">
///     Verified database container identities attached to this deployment target.
/// </param>
internal sealed record AzureLiveLogTarget(
    Guid ProjectId,
    Guid DeploymentId,
    string ResourceId,
    string Revision,
    string AccessToken,
    DateTimeOffset LeaseUntil,
    bool IncludeAppEvents,
    IReadOnlySet<string> DatabaseContainers);

/// <summary>
///     Verified ACA stream address and exact replica/container identity.
/// </summary>
/// <param name="Key">Stable lookup or configuration key.</param>
/// <param name="Uri">Validated provider endpoint URI.</param>
/// <param name="System">Whether this endpoint or record represents platform/system output.</param>
/// <param name="Replica">Verified Container App replica identity.</param>
/// <param name="Container">Verified owned container identity used for observation routing.</param>
internal sealed record AzureLiveLogEndpoint(string Key, Uri Uri, bool System, string Replica, string Container);

/// <summary>
///     Normalized live envelope; provider timestamp and application text remain separate.
/// </summary>
/// <param name="Timestamp">The provider observation timestamp, if known.</param>
/// <param name="Message">The message supplied to the operation.</param>
/// <param name="Revision">Exact verified Azure revision identity.</param>
/// <param name="Stream">The typed diagnostic output stream.</param>
/// <param name="Severity">The normalized severity used to rank a context group.</param>
internal sealed record AzureLiveLogRecord(
    DateTimeOffset Timestamp,
    string Message,
    string Revision,
    DeploymentDiagnosticStream Stream,
    DeploymentDiagnosticSeverity Severity);
