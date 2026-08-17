using System.Text.Json.Serialization;

namespace Services.GitHub;

/// <summary>
///     Repository public key response used for GitHub Actions secret encryption.
/// </summary>
internal sealed record GitHubRepositoryPublicKey(
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("key")] string Key);

/// <summary>
///     Request body used when creating or updating a GitHub Actions repository secret.
/// </summary>
internal sealed record GitHubRepositorySecretRequest(
    [property: JsonPropertyName("encrypted_value")]
    string EncryptedValue,
    [property: JsonPropertyName("key_id")] string KeyId);

/// <summary>
///     Request body used when dispatching a GitHub Actions workflow.
/// </summary>
internal sealed record GitHubWorkflowDispatchRequest([property: JsonPropertyName("ref")] string Ref);