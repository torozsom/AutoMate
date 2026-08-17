using Core.DTO;

namespace Services.Templating;

/// <summary>
///     Applies manifest rule activation and deployment-target matching.
/// </summary>
internal static class TemplateRuleMatcher
{
    /// <summary>
    ///     Determines whether a manifest rule should render for the active deployment configuration.
    /// </summary>
    public static bool ShouldRender(TemplateManifestRuleDto rule, DeploymentConfigDto config)
    {
        if (!rule.IsActive)
            return false;

        var target = string.IsNullOrWhiteSpace(rule.DeploymentTarget)
            ? "All"
            : rule.DeploymentTarget.Trim();

        if (target.Equals("All", StringComparison.OrdinalIgnoreCase))
            return true;

        if (config.IsCloudDeployment)
            return target.Equals("Cloud", StringComparison.OrdinalIgnoreCase);

        return target.Equals("Local", StringComparison.OrdinalIgnoreCase);
    }
}