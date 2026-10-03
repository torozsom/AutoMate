namespace Infrastructure.GitHub;

/// <summary>GitHub App credentials supplied by the SaaS secret provider.</summary>
public sealed class GitHubAppOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GitHubApp";
    /// <summary>Numeric GitHub App identifier.</summary>
    public long AppId { get; set; }
    /// <summary>Public App slug used only for onboarding links.</summary>
    public string AppSlug { get; set; } = string.Empty;
    /// <summary>PEM signing key, provided through a managed secret mount or environment variable.</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;
    /// <summary>Random secret used to authenticate webhook deliveries.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
}
