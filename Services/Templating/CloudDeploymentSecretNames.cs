using System.Security.Cryptography;
using System.Text;

namespace Services.Templating;

/// <summary>
///     Builds deterministic GitHub Actions secret names used by generated cloud deployment workflows.
/// </summary>
public static class CloudDeploymentSecretNames
{
    /// <summary>
    ///     Maximum repository secret name length accepted by GitHub Actions.
    /// </summary>
    private const int MaxGitHubSecretNameLength = 100;

    /// <summary>
    ///     Gets the GitHub Actions secret name for a generated database username.
    /// </summary>
    public static string GetDatabaseUsernameSecretName(int index)
    {
        return $"AUTOMATE_DB_{index}_USERNAME";
    }

    /// <summary>
    ///     Gets the GitHub Actions secret name for a generated database password.
    /// </summary>
    public static string GetDatabasePasswordSecretName(int index)
    {
        return $"AUTOMATE_DB_{index}_PASSWORD";
    }

    /// <summary>
    ///     Gets a deterministic GitHub Actions secret name for a custom environment variable.
    /// </summary>
    public static string GetCustomEnvironmentSecretName(int index, string key)
    {
        var normalizedKey = NormalizeGitHubSecretSegment(key);
        var hash = CreateShortHash(key);
        var secretName = $"AUTOMATE_ENV_{index}_{normalizedKey}_{hash}";

        return secretName.Length <= MaxGitHubSecretNameLength
            ? secretName
            : $"{secretName[..(MaxGitHubSecretNameLength - hash.Length - 1)]}_{hash}";
    }

    /// <summary>
    ///     Normalizes an arbitrary environment variable key into a GitHub secret name segment.
    /// </summary>
    private static string NormalizeGitHubSecretSegment(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray());

        normalized = string.Join('_', normalized
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "VALUE" : normalized;
    }

    /// <summary>
    ///     Creates a stable short hash to disambiguate normalized secret names.
    /// </summary>
    private static string CreateShortHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
    }
}