using Domain.DTO;
using FluentAssertions;
using Infrastructure.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Templating;

/// <summary>Guards the SaaS ACR path against reintroducing long-lived GHCR pull credentials.</summary>
public sealed class CloudRegistryTemplateTests
{
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
}