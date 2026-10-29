using System.Text.Json;
using FluentAssertions;
using Infrastructure.Azure.Databases;
using Test.Infra.TestSupport;

namespace Test.Infra.Azure.Monitor;

/// <summary>
///     Guards database discovery against collecting unrelated resources in a shared group.
/// </summary>
public sealed class AzureManagedDatabaseClientTests
{
    /// <summary>
    ///     Empty provider observations remain an empty metric snapshot rather than an indexing failure.
    /// </summary>
    /// <returns>A task completing after the empty snapshot is verified.</returns>
    [Fact]
    public async Task Missing_observations_return_empty_metric_snapshot()
    {
        var client = new AzureManagedDatabaseClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(_ =>
            DelegateHttpMessageHandler.Json("{\"value\":[]}"))));
        var database = new AzureManagedDatabase(
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Cache/Redis/db", "db",
            "percentProcessorTime,usedmemory");

        var samples = await client.MetricsAsync(database, "token", CancellationToken.None);

        Assert.Empty(samples);
    }

    /// <summary>Dedicated database apps need explicit component tags and use their own revision metrics.</summary>
    [Fact]
    public async Task Dedicated_database_app_requires_component_association_and_uses_its_own_revision()
    {
        var project = Guid.NewGuid();
        const string group = "/subscriptions/sub/resourceGroups/rg";
        const string databaseId = group + "/providers/Microsoft.App/containerApps/database";

        var client = new AzureManagedDatabaseClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/resources"))
                return DelegateHttpMessageHandler.Json($$$"""
                                                          {"value":[{"id":"{{{databaseId}}}","name":"database","type":"Microsoft.App/containerApps",
                                                          "tags":{"automate-project-id":"{{{project:N}}}","automate-container-app":"web",
                                                          "automate-database-component":"postgres","automate-database-provider":"PostgreSQL"}}]}
                                                          """);
            if (request.RequestUri.AbsolutePath.EndsWith("/metrics"))
            {
                Assert.Contains("revisionName eq 'database-revision'",
                    Uri.UnescapeDataString(request.RequestUri.Query));
                Assert.DoesNotContain("web-revision", request.RequestUri.Query);

                return DelegateHttpMessageHandler.Json("""
                                                       {"value":[{"name":{"value":"UsageNanoCores"},"timeseries":[
                                                       {"data":[{"timeStamp":"2026-10-13T10:00:00Z","average":100000000}]},
                                                       {"data":[{"timeStamp":"2026-10-13T10:00:00Z","average":200000000}]}]}]}
                                                       """);
            }

            return DelegateHttpMessageHandler.Json("""
                                                   {"properties":{"latestReadyRevisionName":"database-revision",
                                                   "managedEnvironmentId":"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/env"}}
                                                   """);
        })));
        Assert.Empty(await client.DiscoverAsync(group + "/providers/Microsoft.App/containerApps/web", project, "web",
            "token", CancellationToken.None));
        Assert.Empty(await client.DiscoverAsync(group + "/providers/Microsoft.App/containerApps/web", project, "web",
            "token", CancellationToken.None,
            new HashSet<string> { "unrelated" }));
        var database = Assert.Single(await client.DiscoverAsync(group + "/providers/Microsoft.App/containerApps/web",
            project, "web", "token", CancellationToken.None,
            new HashSet<string> { "postgres" }));
        Assert.Equal("PostgreSQL", database.Provider);
        Assert.Equal("database-revision", database.ContainerAppRevision);
        var observation =
            Assert.Single(await client.MetricObservationsAsync(database, "token", CancellationToken.None));
        Assert.Equal(0.3, Assert.Single(observation.Samples).Value, 6);
    }

    /// <summary>
    ///     SQL child databases require explicit enumeration and their own matching ownership tags.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Discovery_pages_inventory_and_lists_owned_sql_child_databases()
    {
        var project = Guid.NewGuid();
        const string group = "/subscriptions/sub/resourceGroups/rg";
        var reads = new List<string>();
        var client = new AzureManagedDatabaseClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(request =>
        {
            reads.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/databases"))
                return DelegateHttpMessageHandler.Json($$$"""
                                                          {"value":[{"id":"{{{group}}}/providers/Microsoft.Sql/servers/server/databases/owned","name":"owned","type":"Microsoft.Sql/servers/databases","tags":{"automate-project-id":"{{{project:N}}}","automate-container-app":"app"}},
                                                          {"id":"{{{group}}}/providers/Microsoft.Sql/servers/server/databases/master","name":"master","type":"Microsoft.Sql/servers/databases"}]}
                                                          """);
            if (request.RequestUri.Query.Contains("page=2"))
                return DelegateHttpMessageHandler.Json("""{"value":[]}""");

            return DelegateHttpMessageHandler.Json($$$"""
                                                      {"value":[{"id":"{{{group}}}/providers/Microsoft.Sql/servers/server","name":"server","type":"Microsoft.Sql/servers"}],"nextLink":"https://management.azure.com{{{group}}}/resources?page=2"}
                                                      """);
        })));
        var resources = await client.DiscoverAsync(group + "/providers/Microsoft.App/containerApps/app", project, "app",
            "token", CancellationToken.None);

        Assert.Single(resources);
        Assert.Equal("owned", resources[0].Name);
        Assert.Equal(3, reads.Count);
    }

    /// <summary>
    ///     Continuation URLs never forward bearer tokens to another authority or resource group.
    /// </summary>
    /// <param name="continuation">Continuation cursor used by the bounded read.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("https://evil.example/subscriptions/sub/resourceGroups/rg/resources")]
    [InlineData("https://management.azure.com/subscriptions/sub/resourceGroups/other/resources")]
    [InlineData("http://management.azure.com/subscriptions/sub/resourceGroups/rg/resources")]
    public async Task Discovery_rejects_untrusted_continuations(string continuation)
    {
        var reads = 0;
        var client = new AzureManagedDatabaseClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(_ =>
        {
            reads++;

            return DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new
                { value = Array.Empty<object>(), nextLink = continuation }));
        })));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverAsync(
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/app", Guid.NewGuid(), "app",
            "token", CancellationToken.None));

        Assert.Equal(1, reads);
    }

    /// <summary>
    ///     Both owner tags and the verified resource-group prefix are required.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Discovery_excludes_other_projects_apps_groups_and_unsupported_resources()
    {
        var project = Guid.NewGuid();
        var resources = new[]
        {
            new
            {
                id = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Cache/Redis/owned", name = "owned",
                type = "Microsoft.Cache/Redis",
                tags = new Dictionary<string, string>
                    { ["automate-project-id"] = project.ToString("N"), ["automate-container-app"] = "app" }
            },
            new
            {
                id = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Cache/Redis/other", name = "other",
                type = "Microsoft.Cache/Redis",
                tags = new Dictionary<string, string>
                    { ["automate-project-id"] = Guid.NewGuid().ToString("N"), ["automate-container-app"] = "app" }
            },
            new
            {
                id = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Cache/Redis/sibling", name = "sibling",
                type = "Microsoft.Cache/Redis",
                tags = new Dictionary<string, string>
                    { ["automate-project-id"] = project.ToString("N"), ["automate-container-app"] = "sibling" }
            },
            new
            {
                id = "/subscriptions/sub/resourceGroups/elsewhere/providers/Microsoft.Cache/Redis/outside",
                name = "outside", type = "Microsoft.Cache/Redis",
                tags = new Dictionary<string, string>
                    { ["automate-project-id"] = project.ToString("N"), ["automate-container-app"] = "app" }
            },
            new
            {
                id = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage",
                name = "storage", type = "Microsoft.Storage/storageAccounts",
                tags = new Dictionary<string, string>
                    { ["automate-project-id"] = project.ToString("N"), ["automate-container-app"] = "app" }
            }
        };
        var client = new AzureManagedDatabaseClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(_ =>
            DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new { value = resources })))));
        var result = await client.DiscoverAsync(
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/app", project, "app", "token",
            CancellationToken.None);

        result.Should().ContainSingle().Which.Name.Should().Be("owned");
    }

    /// <summary>
    ///     Redis gauges support Maximum; querying Average would silently suppress its observations.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Redis_uses_supported_maximum_aggregation_and_preserves_bytes()
    {
        var client = new AzureManagedDatabaseClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(request =>
        {
            Assert.Contains("aggregation=Maximum", request.RequestUri!.Query);
            Assert.DoesNotContain("%2B", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Z%2F", request.RequestUri.Query);

            return DelegateHttpMessageHandler.Json("""
                                                   {"value":[{"name":{"value":"percentProcessorTime"},"timeseries":[{"data":[{"timeStamp":"2026-10-10T10:01:00Z","maximum":25}]}]},
                                                   {"name":{"value":"usedmemory"},"timeseries":[{"data":[{"timeStamp":"2026-10-10T10:01:00Z","maximum":1048576}]}]}]}
                                                   """);
        })));

        var samples = await client.MetricsAsync(new AzureManagedDatabase(
                "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Cache/Redis/db", "db",
                "percentProcessorTime,usedmemory"),
            "token", CancellationToken.None);

        Assert.Equal(25,
            Assert.Single(samples, s => s is { Name: "automate_cpu_usage_percent", Unit: "percent" }).Value,
            6);
        Assert.Equal(1048576, Assert.Single(samples,
            s => s is { Name: "automate_memory_used_bytes", Unit: "bytes" }).Value, 6);
    }
}
