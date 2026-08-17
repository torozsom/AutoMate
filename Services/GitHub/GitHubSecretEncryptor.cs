using System.Text;
using Sodium;

namespace Services.GitHub;

/// <summary>
///     Encrypts GitHub Actions repository secrets using GitHub's sealed box requirement.
/// </summary>
internal static class GitHubSecretEncryptor
{
    /// <summary>
    ///     Encrypts a secret value using the repository public key returned by GitHub.
    /// </summary>
    public static string EncryptSecret(string secretValue, string base64PublicKey)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secretValue);
        var publicKeyBytes = Convert.FromBase64String(base64PublicKey);
        var encryptedBytes = SealedPublicKeyBox.Create(secretBytes, publicKeyBytes);
        return Convert.ToBase64String(encryptedBytes);
    }
}