using System.Text.Json;
using Domain.DTO.Deployment;
using Domain.DTO.Templating;
using Infrastructure.Templating.Catalog;

namespace Test.Infra.Templating;

/// <summary>Protects manifest deserialization and target/activation policy used for artifact selection.</summary>
public sealed class TemplateManifestContractTests
{
    /// <summary>Explicit destinations and target/activation overrides survive the same JSON boundary as catalog files.</summary>
    [Fact]
    public void Manifest_overrides_preserve_artifact_selection()
    {
        var expected = new TemplateManifestRuleDto
        {
            TemplateFile = "dockerfile.scriban",
            OutputFile = ".automate/Dockerfile",
            DeploymentTarget = " Cloud ",
            IsActive = false
        };
        var actual = JsonSerializer.Deserialize<TemplateManifestRuleDto>(JsonSerializer.Serialize(expected))!;
        Assert.Equal(expected.TemplateFile, actual.TemplateFile);
        Assert.Equal(expected.OutputFile, actual.OutputFile);
        Assert.False(TemplateRuleMatcher.ShouldRender(actual, new DeploymentConfigDto { IsCloudDeployment = true }));
        var active = actual with { IsActive = true };
        Assert.True(TemplateRuleMatcher.ShouldRender(active, new DeploymentConfigDto { IsCloudDeployment = true }));
        Assert.False(TemplateRuleMatcher.ShouldRender(active, new DeploymentConfigDto { IsCloudDeployment = false }));
    }
}
