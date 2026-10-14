using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Azure;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Test.Infra.TestSupport;

namespace Test.Infra.Azure;

/// <summary>
///     Verifies isolated Azure runtime source collection and durable acknowledgement using
///     synthetic provider responses.
/// </summary>
public sealed class AzureContainerAppRuntimeStreamerTests
{
    /// <summary>
    ///     Revisionless platform events belong to the newest active deployment without leaking foreign
    ///     revision logs.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task App_system_events_are_collected_only_by_latest_active_deployment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        await SeedDeploymentAsync(options, protector, older);
        await using var seeded = new AutoMateDbContext(options, protector);
        var first = await seeded.Deployments.SingleAsync();
        var app = await seeded.Applications.SingleAsync();
        const string resource = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/app";
        first.CloudResourceId = resource;
        first.CloudContainerRevision = "rev-1";
        first.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        seeded.Deployments.Add(new Deployment
        {
            Id = newer,
            CsProjectId = first.CsProjectId,
            Status = DeploymentStatus.Running,
            CloudResourceId = resource,
            CloudContainerRevision = "rev-2",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await seeded.SaveChangesAsync();
        using var services = new ServiceCollection()
            .AddScoped<AutoMateDbContext>(_ => new AutoMateDbContext(options, protector)).BuildServiceProvider();
        var publisher = new RecordingPublisher();
        var factory = new StubHttpClientFactory(new DelegateHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "management.azure.com") return Respond(request);
            var query = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!query.Contains("ContainerAppSystemLogs")) return DelegateHttpMessageHandler.Json("""{"tables":[]}""");
            return DelegateHttpMessageHandler.Json("""
                                                   {"tables":[{"columns":[{"name":"TimeGenerated"},{"name":"Message"},{"name":"RevisionName"}],
                                                    "rows":[["2026-10-11T12:00:00Z","app-wide",""],
                                                            ["2026-10-11T12:00:01Z","first","rev-1"],
                                                            ["2026-10-11T12:00:02Z","second","rev-2"]]}]}
                                                   """);
        }));
        // Sequential collectors keep this in-memory SQLite fixture single-connection while sharing verified metadata.
        foreach (var item in new[] { (Id: older, Revision: "rev-1"), (Id: newer, Revision: "rev-2") })
        {
            using var collector = new AzureContainerAppRuntimeStreamer(publisher, new SuccessfulTokenProvider(),
                factory,
                services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AzureMonitorLogsOptions()),
                NullLogger<AzureContainerAppRuntimeStreamer>.Instance);
            collector.StartStreaming(new AzureContainerAppRuntimeStreamRequest
            {
                ProjectId = app.Id,
                UserId = app.UserId,
                DeploymentId = item.Id,
                ExpectedRevision = item.Revision,
                AzureCredentials = new AzureCloudCredentialsDto { SubscriptionId = "sub", AccessToken = "token" },
                Config = new DeploymentConfigDto { CloudResourceGroupName = "rg", CloudContainerAppName = "app" }
            });
            await collector.PollOnceAsync(default);
        }

        Assert.Contains(publisher.Events, e => e.DeploymentId == newer && e.Message == "[Azure system] app-wide");
        Assert.DoesNotContain(publisher.Events, e => e.DeploymentId == older && e.Message.Contains("app-wide"));
        Assert.DoesNotContain(publisher.Events, e => e.DeploymentId == older && e.Message == "[Azure system] second");
        Assert.DoesNotContain(publisher.Events, e => e.DeploymentId == newer && e.Message == "[Azure system] first");
    }

    /// <summary>
    ///     Restarted collectors accept delayed equal-ingestion events only after durable
    ///     acknowledgement.
    /// </summary>
    /// <param name="accepted">Expected admission or operation success.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delayed_ingestion_survives_restart_without_advancing_rejected_records(bool accepted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        var deployment = Guid.NewGuid();
        await SeedDeploymentAsync(options, protector, deployment);
        await using var seeded = new AutoMateDbContext(options, protector);
        var app = await seeded.Applications.SingleAsync();
        var ingestion = DateTimeOffset.Parse("2026-10-10T11:00:00Z");
        seeded.AzureContainerAppLogCheckpoints.Add(new AzureContainerAppLogCheckpoint
        {
            DeploymentId = deployment,
            Source = "console",
            LastTimestamp = ingestion,
            LastTieBreaker = "hash-a",
            LastIngestionTimestamp = ingestion,
            LastIngestionTieBreaker = "hash-a"
        });
        await seeded.SaveChangesAsync();
        using var services = new ServiceCollection()
            .AddScoped<AutoMateDbContext>(_ => new AutoMateDbContext(options, protector)).BuildServiceProvider();
        var publisher = new RecordingPublisher { Accepted = accepted };
        var handler = new DelegateHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "management.azure.com") return Respond(request);
            var query = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement
                .GetProperty("query").GetString()!;
            if (!query.Contains("ContainerAppConsoleLogs")) return DelegateHttpMessageHandler.Json("""{"tables":[]}""");
            Assert.Contains("strcmp(CursorHash, 'hash-", query);
            return DelegateHttpMessageHandler.Json("""
                                                   {"tables":[{"columns":[{"name":"TimeGenerated"},{"name":"IngestedAt"},{"name":"Message"},{"name":"RevisionName"},{"name":"CursorHash"}],"rows":[["2026-10-10T09:00:00Z","2026-10-10T11:00:00Z","late","rev-1","hash-b"]]}]}
                                                   """);
        });
        for (var restart = 0; restart < 2; restart++)
        {
            using var streamer = new AzureContainerAppRuntimeStreamer(publisher, new SuccessfulTokenProvider(),
                new StubHttpClientFactory(handler), services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AzureMonitorLogsOptions()), NullLogger<AzureContainerAppRuntimeStreamer>.Instance);
            streamer.StartStreaming(new AzureContainerAppRuntimeStreamRequest
            {
                ProjectId = app.Id,
                UserId = app.UserId,
                DeploymentId = deployment,
                ExpectedRevision = "rev-1",
                AzureCredentials = new AzureCloudCredentialsDto { SubscriptionId = "sub", AccessToken = "token" },
                Config = new DeploymentConfigDto { CloudResourceGroupName = "rg", CloudContainerAppName = "app" }
            });
            await streamer.PollOnceAsync(default);
        }

        var events = publisher.Events.Where(e => e.Message == "late").ToArray();
        Assert.Equal(accepted ? 1 : 2, events.Length);
        Assert.Single(events.Select(e => e.EventId).Distinct());
        Assert.All(events, e => Assert.Equal(ingestion.AddHours(-2), e.TimestampUtc));
        await using var check = new AutoMateDbContext(options, protector);
        var checkpoint = await check.AzureContainerAppLogCheckpoints.SingleAsync(c => c.Source == "console");
        Assert.Equal(accepted ? "hash-b" : "hash-a", checkpoint.LastIngestionTieBreaker);
    }

    /// <summary>
    ///     Verifies that parallel deployments in one project keep distinct revision logs.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Parallel_deployments_in_one_project_keep_distinct_revision_logs()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AutoMate.Deployments",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var path = Path.Combine(Path.GetTempPath(), "automate-azure-" + Guid.NewGuid().ToString("N") + ".db");
        var dbOptions = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        var protector = new EphemeralDataProtectionProvider();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        try
        {
            await SeedDeploymentAsync(dbOptions, protector, first);
            await using var seeded = new AutoMateDbContext(dbOptions, protector);
            var project = await seeded.Applications.SingleAsync();
            seeded.Deployments.Add(new Deployment
            { Id = second, CsProjectId = (await seeded.Deployments.SingleAsync()).CsProjectId });
            await seeded.SaveChangesAsync();
            using var services = new ServiceCollection()
                .AddScoped<AutoMateDbContext>(_ => new AutoMateDbContext(dbOptions, protector)).BuildServiceProvider();
            var publisher = new RecordingPublisher();
            var handler = new DelegateHttpMessageHandler(request =>
            {
                var response = Respond(request);
                if (request.RequestUri!.Host != "management.azure.com" &&
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("rev-2"))
                {
                    response.Dispose();
                    return DelegateHttpMessageHandler.Json(
                        RecordsJson("second revision line", "stdout", "ContainerAppConsoleLogs")
                            .Replace("rev-1", "rev-2"));
                }

                return response;
            });
            var streamer = new AzureContainerAppRuntimeStreamer(publisher, new SuccessfulTokenProvider(),
                new StubHttpClientFactory(handler), services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AzureMonitorLogsOptions()), NullLogger<AzureContainerAppRuntimeStreamer>.Instance);
            foreach (var target in new[] { (Id: first, Revision: "rev-1"), (Id: second, Revision: "rev-2") })
                streamer.StartStreaming(new AzureContainerAppRuntimeStreamRequest
                {
                    ProjectId = project.Id,
                    UserId = project.UserId,
                    DeploymentId = target.Id,
                    ExpectedRevision = target.Revision,
                    AzureCredentials = new AzureCloudCredentialsDto
                    { SubscriptionId = "sub", AccessToken = "arm-token" },
                    Config = new DeploymentConfigDto { CloudResourceGroupName = "rg", CloudContainerAppName = "app" }
                });
            await streamer.PollOnceAsync(default);
            foreach (var id in new[] { first, second })
            {
                var owned = spans.Where(span => Equals(span.GetTagItem("deployment.id"), id)).ToArray();
                var poll = Assert.Single(owned, span => span.OperationName == "azure.runtime.poll");
                Assert.Contains(owned,
                    span => span.OperationName == "azure.logs.query" && span.TraceId == poll.TraceId);
                Assert.All(owned, span =>
                {
                    Assert.Equal(project.Id, span.GetTagItem("deployment.project.id"));
                    Assert.Empty(span.Events);
                    Assert.Null(span.StatusDescription);
                });
            }

            Assert.Contains(publisher.Events, e => e.DeploymentId == first && e.Message == "console line");
            Assert.Contains(publisher.Events, e => e.DeploymentId == second && e.Message == "second revision line");
            Assert.DoesNotContain(publisher.Events,
                e => (e.DeploymentId == first && e.Message == "second revision line") ||
                     (e.DeploymentId == second && e.Message == "console line"));
            await using var check = new AutoMateDbContext(dbOptions, protector);
            Assert.Equal(2,
                (await check.AzureContainerAppLogCheckpoints.ToListAsync()).Select(c => c.DeploymentId).Distinct()
                .Count());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     Verifies that poll once routes console and system records with deployment correlation.
    /// </summary>
    /// <param name="saved">Whether the fixture deployment already has saved identity metadata.</param>
    /// <param name="accepted">Expected admission or operation success.</param>
    /// <param name="expectedRevision">Exact expected Azure revision identity.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(true, true, "rev-1")]
    [InlineData(false, true, "rev-1")]
    [InlineData(true, false, "rev-1")]
    [InlineData(true, true, "another-deployment-revision")]
    public async Task PollOnce_routes_console_and_system_records_with_deployment_correlation(bool saved, bool accepted,
        string expectedRevision)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        var deploymentId = Guid.NewGuid();
        await SeedDeploymentAsync(dbOptions, protector, deploymentId);
        await using var seeded = new AutoMateDbContext(dbOptions, protector);
        var project = await seeded.Applications.SingleAsync();
        project.RuntimeDiagnosticsEnabled = saved;
        await seeded.SaveChangesAsync();
        var viewers = new DeploymentRuntimeViewers(TimeProvider.System);

        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IDeploymentRuntimeViewers>(viewers)
            .AddScoped<AutoMateDbContext>(_ => new AutoMateDbContext(dbOptions, protector))
            .BuildServiceProvider();
        var diagnostics = new RecordingPublisher { Accepted = accepted };
        var streamer = new AzureContainerAppRuntimeStreamer(diagnostics, new SuccessfulTokenProvider(),
            new StubHttpClientFactory(new DelegateHttpMessageHandler(Respond)),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AzureMonitorLogsOptions { InitialLookback = TimeSpan.FromMinutes(1) }),
            NullLogger<AzureContainerAppRuntimeStreamer>.Instance);

        streamer.StartStreaming(new AzureContainerAppRuntimeStreamRequest
        {
            ProjectId = project.Id,
            DeploymentId = deploymentId,
            UserId = project.UserId,
            ExpectedRevision = expectedRevision,
            AzureCredentials = new AzureCloudCredentialsDto { SubscriptionId = "sub", AccessToken = "arm-token" },
            Config = new DeploymentConfigDto { CloudResourceGroupName = "rg", CloudContainerAppName = "app" }
        });


        await streamer.PollOnceAsync(CancellationToken.None);

        if (expectedRevision != "rev-1")
        {
            Assert.DoesNotContain(diagnostics.Events,
                e => e.Message == "console line" || e.Message.Contains("revision provisioned"));
            await using var otherRevisionDb = new AutoMateDbContext(dbOptions, protector);
            Assert.All(await otherRevisionDb.AzureContainerAppLogCheckpoints.ToListAsync(),
                c => Assert.Null(c.LastTimestamp));
            return;
        }

        diagnostics.Events.Should().Contain(eventItem => eventItem.DeploymentId == deploymentId &&
                                                         eventItem.Message == "console line" &&
                                                         eventItem.TerminalChannel ==
                                                         new DeploymentTerminalChannel(
                                                             DeploymentTerminalChannelKind.Container, "cloud-web"));
        diagnostics.Events.Should().Contain(eventItem => eventItem.DeploymentId == deploymentId &&
                                                         eventItem.Message == "[Azure system] revision provisioned" &&
                                                         eventItem.TerminalChannel.Kind ==
                                                         DeploymentTerminalChannelKind.System &&
                                                         eventItem.SourceIdentity!.Stream ==
                                                         DeploymentDiagnosticStream.System);
        await using var checkpointsDb = new AutoMateDbContext(dbOptions, protector);
        var checkpoints = await checkpointsDb.AzureContainerAppLogCheckpoints.ToListAsync();
        Assert.All(checkpoints, c => Assert.Equal(accepted, c.LastTimestamp is not null));
    }

    /// <summary>
    ///     Managed DB diagnostics keep native units and retry unconfirmed logs without cursor loss.
    /// </summary>
    /// <param name="accepted">Expected admission or operation success.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Managed_database_logs_and_metrics_use_verified_ownership_and_durable_checkpoints(bool accepted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        var protector = new EphemeralDataProtectionProvider();
        var deploymentId = Guid.NewGuid();
        await SeedDeploymentAsync(dbOptions, protector, deploymentId);
        await using var seeded = new AutoMateDbContext(dbOptions, protector);
        var project = await seeded.Applications.SingleAsync();
        using var services = new ServiceCollection()
            .AddScoped<AutoMateDbContext>(_ => new AutoMateDbContext(dbOptions, protector)).BuildServiceProvider();
        var publisher = new RecordingPublisher { Accepted = accepted };
        const string dbResource =
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.DBforPostgreSQL/flexibleServers/db";
        var handler = new DelegateHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/resources"))
                return DelegateHttpMessageHandler.Json($$$"""
                                                          {"value":[{"id":"{{{dbResource}}}","name":"db","type":"Microsoft.DBforPostgreSQL/flexibleServers",
                                                          "tags":{"automate-project-id":"{{{project.Id:N}}}","automate-container-app":"app"}}]}
                                                          """);
            if (request.RequestUri.AbsolutePath.Contains("flexibleServers/db"))
            {
                if (request.RequestUri.Host == "management.azure.com")
                    return DelegateHttpMessageHandler.Json("""
                                                           {"value":[{"name":{"value":"cpu_percent"},"timeseries":[{"data":[{"timeStamp":"2026-10-10T10:01:00Z","average":42}]}]},
                                                           {"name":{"value":"memory_percent"},"timeseries":[{"data":[{"timeStamp":"2026-10-10T10:01:00Z","average":65}]}]}]}
                                                           """);
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var query = body.RootElement.GetProperty("query").GetString()!;
                var lowerBound = Regex.Match(query,
                    @"IngestedAt >= datetime\(([^)]+)\)").Groups[1].Value;
                Assert.True(DateTimeOffset.Parse(lowerBound) <= DateTimeOffset.UtcNow.AddHours(-23),
                    "Initial database collection must recover quiet startup events preceding web readiness.");
                return DelegateHttpMessageHandler.Json(RecordsJson("database line", "system", "AzureDiagnostics"));
            }

            // An ARM transport failure must not prevent either console or DB log collection.
            if (request.RequestUri.AbsolutePath.Contains("containerApps/app/providers/microsoft.insights"))
            {
                Assert.Contains(publisher.Events, e =>
                    e.SourceIdentity?.Component == DeploymentDiagnosticComponent.Database &&
                    e.Kind == DeploymentDiagnosticKind.Metric);
                throw new HttpRequestException("synthetic provider detail");
            }

            return Respond(request);
        });
        using var streamer = new AzureContainerAppRuntimeStreamer(publisher, new SuccessfulTokenProvider(),
            new StubHttpClientFactory(handler), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AzureMonitorLogsOptions()), NullLogger<AzureContainerAppRuntimeStreamer>.Instance);
        streamer.StartStreaming(new AzureContainerAppRuntimeStreamRequest
        {
            ProjectId = project.Id,
            DeploymentId = deploymentId,
            UserId = project.UserId,
            ExpectedRevision = "rev-1",
            AzureCredentials = new AzureCloudCredentialsDto { SubscriptionId = "sub", AccessToken = "token" },
            Config = new DeploymentConfigDto { CloudResourceGroupName = "rg", CloudContainerAppName = "app" }
        });
        await streamer.PollOnceAsync(default);
        Assert.Contains(publisher.Events, e => e.Message == "console line");
        Assert.Contains(publisher.Events,
            e => e.TerminalChannel.Target == "azure-db" && e.Message == "[db] database line");
        var metric = Assert.Single(publisher.Events,
            e => e.SourceIdentity?.Component == DeploymentDiagnosticComponent.Database &&
                 e.Kind == DeploymentDiagnosticKind.Metric);
        Assert.Contains(metric.Metrics!,
            m => m.Name == "automate_cpu_usage_percent" && m.Value == 42 && m.Unit == "percent");
        Assert.Contains(metric.Metrics!, m => m.Name == "automate_memory_used_percent" && m.Value == 65);
        await using var check = new AutoMateDbContext(dbOptions, protector);
        var cursor = Assert.Single(await check.AzureContainerAppLogCheckpoints.ToListAsync(),
            c => c.Source.StartsWith("db-"));
        Assert.Equal(accepted, cursor.LastTimestamp is not null);
        await streamer.PollOnceAsync(default);
        Assert.Equal(accepted ? 1 : 2, publisher.Events.Count(e => e.TerminalChannel.Target == "azure-db" &&
                                                                   e.Kind == DeploymentDiagnosticKind.Log));
        Assert.DoesNotContain(publisher.Events, e => e.TerminalChannel.Target == "azure-db" &&
                                                     e.SourceIdentity?.Stream == DeploymentDiagnosticStream.Metric);
        Assert.DoesNotContain(publisher.Events, e => e.Message.Contains("synthetic provider detail"));
    }

    /// <summary>
    ///     Returns synthetic ARM or Log Analytics responses for the requested fixture endpoint.
    /// </summary>
    /// <param name="request">Synthetic HTTP request observed by the handler.</param>
    /// <returns>The configured HTTP response message instance or response for this scenario.</returns>
    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (request.RequestUri!.Host == "management.azure.com")
        {
            if (request.RequestUri.AbsoluteUri.Contains("/metrics?", StringComparison.Ordinal))
                return DelegateHttpMessageHandler.Json("{ \"value\": [] }");
            return DelegateHttpMessageHandler.Json(
                "{ \"properties\": { \"latestReadyRevisionName\": \"rev-1\", \"managedEnvironmentId\": \"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/env\" } }");
        }

        Assert.Contains("/managedEnvironments/env/query", request.RequestUri.AbsolutePath);
        var query = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement
            .GetProperty("query").GetString()!;
        return DelegateHttpMessageHandler.Json(query.Contains("ContainerAppConsoleLogs", StringComparison.Ordinal)
            ? RecordsJson("console line", "stdout", "ContainerAppConsoleLogs")
            : RecordsJson("revision provisioned", "system", "ContainerAppSystemLogs"));
    }

    /// <summary>
    ///     Builds a single synthetic Azure diagnostic row with the supplied message, stream and table.
    /// </summary>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <param name="stream">Synthetic output stream or destination used by the fixture.</param>
    /// <param name="sourceTable">Recorded Azure diagnostic category or table.</param>
    /// <returns>The synthetic text produced for this scenario.</returns>
    private static string RecordsJson(string message, string stream, string sourceTable)
    {
        return $$"""
                 { "tables": [{ "columns": [
                   { "name": "TimeGenerated" }, { "name": "Message" }, { "name": "ContainerName" },
                   { "name": "RevisionName" }, { "name": "Stream" }, { "name": "SourceTable" }],
                   "rows": [["2026-09-28T10:00:00Z", "{{message}}", "web", "rev-1", "{{stream}}", "{{sourceTable}}"]] }] }
                 """;
    }

    /// <summary>
    ///     Creates fixture metadata and saves a running deployment for the supplied identity.
    /// </summary>
    /// <param name="options">EF options for the fixture metadata database.</param>
    /// <param name="protector">Ephemeral fixture data-protection provider.</param>
    /// <param name="deploymentId">Synthetic deployment identity.</param>
    /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
    private static async Task SeedDeploymentAsync(DbContextOptions<AutoMateDbContext> options,
        IDataProtectionProvider protector, Guid deploymentId)
    {
        await using var dbContext = new AutoMateDbContext(options, protector);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            Status = DeploymentStatus.Running,
            CsProject = new CsProject
            {
                Name = "Web",
                Path = "Web/Web.csproj",
                Application = new Domain.Entities.Application
                {
                    Name = "Sample",
                    SourceType = SourceType.Remote,
                    RuntimeDiagnosticsEnabled = true,
                    SourcePathOrUrl = "https://github.com/example/sample",
                    User = new LocalUser { Username = "test", Email = "test@example.invalid" }
                }
            }
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    ///     Records normalized observations and returns the configured durable acknowledgement.
    /// </summary>
    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher, IDurableDeploymentDiagnosticPublisher
    {
        /// <summary>Configured durable acknowledgement returned by the synthetic publisher.</summary>
        public bool Accepted { get; init; } = true;

        /// <summary>Synthetic events captured or supplied by this fixture.</summary>
        public List<DeploymentDiagnosticEvent> Events { get; } = [];

        /// <summary>
        ///     Records normalized fixture observations and reports the configured publication outcome.
        /// </summary>
        /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add(diagnosticEvent);
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        ///     Records normalized fixture observations and reports the configured publication outcome.
        /// </summary>
        /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured fixture condition or completion outcome.</returns>
        public Task<bool> PublishDurablyAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add(diagnosticEvent);
            }

            return Task.FromResult(Accepted);
        }
    }

    /// <summary>
    ///     Supplies a fixed synthetic Log Analytics access token without external authentication.
    /// </summary>
    private sealed class SuccessfulTokenProvider : IAzureMonitorLogsTokenProvider
    {
        /// <summary>
        ///     Supplies a fixed synthetic Log Analytics token without an external token exchange.
        /// </summary>
        /// <param name="userId">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured Azure monitor logs token result instance or response for this scenario.</returns>
        public Task<AzureMonitorLogsTokenResult> GetTokenAsync(Guid userId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AzureMonitorLogsTokenResult("monitor-token", null));
        }
    }
}
