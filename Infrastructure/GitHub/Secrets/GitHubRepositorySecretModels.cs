using System.Text.Json.Serialization;

namespace Infrastructure.GitHub;

/// <summary>
///     Repository public key response used for GitHub Actions secret encryption.
/// </summary>
/// <param name="KeyId">Provider public-key identifier used to encrypt repository secrets.</param>
/// <param name="Key">Stable lookup or configuration key.</param>
internal sealed record GitHubRepositoryPublicKey(
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("key")] string Key);

/// <summary>
///     Request body used when creating or updating a GitHub Actions repository secret.
/// </summary>
/// <param name="EncryptedValue">Encrypted secret value; never log it.</param>
/// <param name="KeyId">Provider public-key identifier used to encrypt repository secrets.</param>
internal sealed record GitHubRepositorySecretRequest(
    [property: JsonPropertyName("encrypted_value")]
    string EncryptedValue,
    [property: JsonPropertyName("key_id")] string KeyId);

/// <summary>
///     Request body used when dispatching a GitHub Actions workflow.
/// </summary>
/// <param name="Ref">Provider repository reference identifying the selected branch or commit.</param>
internal sealed record GitHubWorkflowDispatchRequest([property: JsonPropertyName("ref")] string Ref);
