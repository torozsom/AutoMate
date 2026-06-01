using System.Security.Cryptography;
using System.Text;

namespace Services.Templating;

/// <summary>
///     Builds deterministic GitHub Actions secret names used by generated cloud deployment workflows.
/// </summary>
public static class CloudDeploymentSecretNames
{
    private const int MaxGitHubSecretNameLength = 100;

    public static string GetDatabaseUsernameSecretName(int index)
    {
        return $"AUTOMATE_DB_{index}_USERNAME";
    }

    public static string GetDatabasePasswordSecretName(int index)
    {
        return $"AUTOMATE_DB_{index}_PASSWORD";
    }

    public static string GetCustomEnvironmentSecretName(int index, string key)
    {
        var normalizedKey = NormalizeGitHubSecretSegment(key);
        var hash = CreateShortHash(key);
        var secretName = $"AUTOMATE_ENV_{index}_{normalizedKey}_{hash}";

        return secretName.Length <= MaxGitHubSecretNameLength
            ? secretName
            : $"{secretName[..(MaxGitHubSecretNameLength - hash.Length - 1)]}_{hash}";
    }

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

    private static string CreateShortHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
    }
}
