using System.Text.Json;
using Domain.Entities.Deployments;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared.Configuration;

/// <summary>
///     Presents recorded launch settings with safe links and explicitly selected metadata copying.
/// </summary>
public partial class DeploymentConfigurationDetails
{
    /// <summary>Selected deployment; artifacts must not come from another run.</summary>
    [Parameter]
    public Deployment? Deployment { get; set; }

    /// <summary>Only an explicit local provider makes cloud fields inapplicable.</summary>
    private string CloudMissing =>
        Snapshot?.Provider is { } provider && (provider.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
                                               provider.Contains("Local", StringComparison.OrdinalIgnoreCase))
            ? "Not applicable"
            : "Not recorded";

    /// <summary>Reads only the explicit non-secret snapshot contract.</summary>
    private DeploymentConfigurationSnapshot? Snapshot
    {
        get
        {
            try
            {
                return Deployment?.ConfigurationSnapshotJson is { } json
                    ? JsonSerializer.Deserialize<DeploymentConfigurationSnapshot>(json)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Validated source from the immutable snapshot, or recorded legacy repository identity.</summary>
    private string? SourceUrl => SafeUrl(Snapshot?.SourceUrl, true);

    /// <summary>Stable run link survives workflow completion and component reload.</summary>
    private string? WorkflowUrl => SourceUrl is { } source
        ? (source.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase)
              ? source.TrimEnd('/')[..^4]
              : source.TrimEnd('/')) +
          (Deployment?.CloudGitHubActionRunId is { } run ? $"/actions/runs/{run}" : "/actions/workflows/deploy.yml")
        : null;

    /// <summary>Recorded cloud endpoint or local host port, without asserting that old resources still exist.</summary>
    private string? AppUrl => SafeUrl(Deployment?.CloudAppUrl) ?? (Deployment?.ResolvedHostPort is { } port
        ? $"http://localhost:{port}"
        : null);

    /// <summary>Azure Portal deep link constructed from a recorded ARM identity.</summary>
    private string? ResourceUrl => Deployment?.CloudResourceId is { } id
                                   && id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
        ? "https://portal.azure.com/#resource" + id
        : null;

    /// <summary>
    ///     External links reject credential-bearing URLs and unsafe schemes.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <param name="github">Whether to additionally restrict the link to GitHub.</param>
    /// <returns>The extracted or validated string, or null when absent or invalid.</returns>
    private static string? SafeUrl(string? value, bool github = false)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
               (!github || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            ? uri.GetLeftPart(UriPartial.Path)
            : null;
    }
}
