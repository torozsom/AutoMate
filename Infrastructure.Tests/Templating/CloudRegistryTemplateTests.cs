using Domain.DTO;
using FluentAssertions;
using Infrastructure.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Templating;

/// <summary>Guards the SaaS ACR path against reintroducing long-lived GHCR pull credentials.</summary>
public sealed class CloudRegistryTemplateTests
{
    /// <summary>Both registry paths resume the resource-level state left by a previous stop.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("customer.azurecr.io")]
    public async Task Workflow_starts_the_deployed_container_app(string registry)
    {
        var files = await new TemplatingService(NullLogger<TemplatingService>.Instance).GenerateAllTemplatesAsync(
            new DeploymentConfigDto { IsCloudDeployment = true, ProjectName = "app", CloudRegistryName = registry },
            new ProjectMetadataDto { DotNetVersion = "10.0", IsWebProject = true }, "app", ".");
        var workflow = files.Single(f => f.Path == ".github/workflows/deploy.yml").Content;
        workflow.Should().Contain("Start deployed container app")
            .And.Contain("/start?api-version=2025-07-01")
            .And.Contain("--only-show-errors --output none");
        workflow.IndexOf("Start deployed container app", StringComparison.Ordinal).Should().BeGreaterThan(
            workflow.IndexOf("Deploy infrastructure", StringComparison.Ordinal));
    }

    /// <summary>Generated SaaS assets use OIDC push and pull-only managed identity.</summary>
    [Fact]
    public async Task Azure_registry_templates_do_not_reference_ghcr_pat()
    {
        var service = new TemplatingService(NullLogger<TemplatingService>.Instance);
        var files = await service.GenerateAllTemplatesAsync(new DeploymentConfigDto
        {
            IsCloudDeployment = true,
            ProjectName = "sample-web",
            CloudRegistryName = "customer.azurecr.io",
            CloudResourceGroupName = "sample-rg",
            CloudContainerAppName = "sample-app"
        }, new ProjectMetadataDto { DotNetVersion = "10.0", IsWebProject = true }, "sample-web", ".");

        var workflow = files.Single(item => item.Path == ".github/workflows/deploy.yml").Content;
        var bicep = files.Single(item => item.Path == "infra/main.bicep").Content;
        workflow.Should().Contain("az acr login --name customer");
        workflow.Should().Contain("customer.azurecr.io/sample-web");
        workflow.Should().Contain("AZURE_ACR_PULL_IDENTITY_ID");
        workflow.Should().NotContain("GHCR_PAT").And.NotContain("ghcr.io")
            .And.NotContain("packages: write").And.NotContain("      - main");
        bicep.Should().Contain("identity: registryPullIdentityId");
        bicep.Should().NotContain("containerRegistryPassword");
    }

    /// <summary>Every generated managed DB receives owner tags and workspace diagnostic export.</summary>
    [Theory]
    [InlineData("PostgreSQL", "postgres0")]
    [InlineData("MySQL", "mysql0")]
    [InlineData("SQLServer", "sqlServer0Database")]
    [InlineData("MongoDB", "cosmosMongo0")]
    [InlineData("Redis", "redis0")]
    public async Task Managed_database_templates_enable_owned_resource_diagnostics(string type, string scope)
    {
        var project = Guid.NewGuid();
        var files = await new TemplatingService(NullLogger<TemplatingService>.Instance).GenerateAllTemplatesAsync(
            new DeploymentConfigDto
            {
                ProjectId = project, IsCloudDeployment = true, ProjectName = "app",
                Databases = [new DatabaseConfigDto { DbType = type }]
            },
            new ProjectMetadataDto { DotNetVersion = "10.0", IsWebProject = true }, "app", ".");
        var bicep = files.Single(f => f.Path == "infra/main.bicep").Content;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AutoMate.slnx"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, ".artifacts", "azure-runtime");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, type + ".bicep"), bicep);
        bicep.Should().Contain("'automate-project-id': '" + project.ToString("N") + "'")
            .And.Contain("tags: telemetryTags").And.NotContain("categoryGroup: 'allLogs'");
        if (type is "MySQL" or "Redis") bicep.Should().NotContain("diagnosticSettings");
        else
            bicep.Should().Contain("scope: " + scope).And.Contain("workspaceId: logAnalytics.id")
                .And.Contain("logAnalyticsDestinationType: 'AzureDiagnostics'");
    }
}