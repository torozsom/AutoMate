using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Templating;

/// <summary>
///     Builds deterministic GitHub Actions secret names used by generated cloud deployment
///     workflows.
/// </summary>
public static class CloudDeploymentSecretNames
{
    /// <summary>Maximum repository secret name length accepted by GitHub Actions.</summary>
    private const int MaxGitHubSecretNameLength = 100;

    /// <summary>
    ///     Gets the GitHub Actions secret name for a generated database username.
    /// </summary>
    /// <param name="index">Validated numeric position or durable lookup identity.</param>
    /// <returns>The protected or scoped credential value; it must never enter diagnostics.</returns>
    public static string GetDatabaseUsernameSecretName(int index)
    {
        return $"AUTOMATE_DB_{index}_USERNAME";
    }

    /// <summary>
    ///     Gets the GitHub Actions secret name for a generated database password.
    /// </summary>
    /// <param name="index">Validated numeric position or durable lookup identity.</param>
    /// <returns>The protected or scoped credential value; it must never enter diagnostics.</returns>
    public static string GetDatabasePasswordSecretName(int index)
    {
        return $"AUTOMATE_DB_{index}_PASSWORD";
    }

    /// <summary>
    ///     Gets a deterministic GitHub Actions secret name for a custom environment variable.
    /// </summary>
    /// <param name="index">Validated numeric position or durable lookup identity.</param>
    /// <param name="key">Stable lookup or configuration key.</param>
    /// <returns>The protected or scoped credential value; it must never enter diagnostics.</returns>
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
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The protected or scoped credential value; it must never enter diagnostics.</returns>
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
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The stable identity derived from the documented metadata or content.</returns>
    private static string CreateShortHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
    }
}