using System.Text;
using Sodium;

namespace Infrastructure.GitHub.Secrets;

/// <summary>
///     Encrypts GitHub Actions repository secrets using GitHub's sealed box requirement.
/// </summary>
internal static class GitHubSecretEncryptor
{
    /// <summary>
    ///     Encrypts a secret value using the repository public key returned by GitHub.
    /// </summary>
    /// <param name="secretValue">Secret to encrypt for the provider; never log it.</param>
    /// <param name="base64PublicKey">GitHub repository public key encoded as Base64.</param>
    /// <returns>The protected or scoped credential value; it must never enter diagnostics.</returns>
    public static string EncryptSecret(string secretValue, string base64PublicKey)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secretValue);
        var publicKeyBytes = Convert.FromBase64String(base64PublicKey);
        var encryptedBytes = SealedPublicKeyBox.Create(secretBytes, publicKeyBytes);
        return Convert.ToBase64String(encryptedBytes);
    }
}
