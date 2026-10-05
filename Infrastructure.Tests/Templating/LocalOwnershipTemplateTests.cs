using Domain.DTO;
using Infrastructure.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Templating;

/// <summary>Guards generated ownership metadata and existing database/container behavior.</summary>
public sealed class LocalOwnershipTemplateTests
{
    /// <summary>Web ownership varies by process environment; shared databases retain project-scoped labels.</summary>
    [Fact]
    public async Task Compose_labels_preserve_container_names_ports_and_database_lifetime()
    {
        var project = Guid.NewGuid();
        var files = await new TemplatingService(NullLogger<TemplatingService>.Instance).GenerateAllTemplatesAsync(
            new DeploymentConfigDto
            {
                ProjectId = project,
                ProjectName = "sample",
                ExposedPort = 12345,
                Databases = [new DatabaseConfigDto { DbType = "PostgreSQL", ContainerNameSuffix = "db" }]
            },
            new ProjectMetadataDto { DotNetVersion = "10.0", IsWebProject = true }, "sample-web", ".");
        var compose = files.Single(f => f.Path.EndsWith("docker-compose.yml")).Content;
        Assert.Contains(project.ToString("N"), compose);
        Assert.Contains("${AUTOMATE_DEPLOYMENT_ID:-unassigned}", compose);
        Assert.Contains("container_name: sample-web-web", compose);
        Assert.Contains("container_name: sample-db", compose);
        Assert.Contains("\"12345:8080\"", compose);
        var db = compose[compose.LastIndexOf("  sample-db:", StringComparison.Ordinal)..];
        Assert.Contains("io.automate.scope: \"project\"", db);
        Assert.DoesNotContain("io.automate.deployment", db);
        Assert.DoesNotContain("volumes:", compose);
        Assert.Contains("image: postgres:15-alpine", db);
    }
}