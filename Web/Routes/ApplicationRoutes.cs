namespace Web.Routes;

/// <summary>
///     Shared application URL paths for navigation, form actions and authentication endpoint mappings.
/// </summary>
internal static class ApplicationRoutes
{
    /// <summary>Local credential sign-in form action.</summary>
    internal const string LoginAction = "/api/auth/login";

    /// <summary>Authenticated sign-out form action.</summary>
    internal const string LogoutAction = "/api/auth/logout";

    /// <summary>GitHub OAuth challenge endpoint.</summary>
    internal const string GitHubLogin = "/api/auth/github-login";

    /// <summary>Azure account connection endpoint.</summary>
    internal const string AzureLogin = "/api/auth/azure-login";

    /// <summary>Owner-scoped project inventory page.</summary>
    internal const string Dashboard = "/dashboard";

    /// <summary>Connected GitHub repository import page.</summary>
    internal const string GitHubRepositories = "/github-repos";

    /// <summary>Self-hosted local repository import page.</summary>
    internal const string LocalRepositories = "/local-repos";

    /// <summary>Local account sign-in page.</summary>
    internal const string Login = "/login";

    /// <summary>Local account registration page.</summary>
    internal const string Register = "/register";

    /// <summary>Interactive API documentation entry point.</summary>
    internal const string ApiDocumentation = "/swagger";
}
