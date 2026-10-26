# GitHub repository secrets

Provider wire models and sealed-box encryption for Actions repository secrets.

## Contents

| File                                                               | Types or asset                                                                                | Responsibility                                                                    |
|--------------------------------------------------------------------|-----------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------|
| [GitHubRepositorySecretModels.cs](GitHubRepositorySecretModels.cs) | `GitHubRepositoryPublicKey`, `GitHubRepositorySecretRequest`, `GitHubWorkflowDispatchRequest` | Wire shapes for public keys, encrypted repository secrets and workflow dispatch.  |
| [GitHubSecretEncryptor.cs](GitHubSecretEncryptor.cs)               | `GitHubSecretEncryptor`                                                                       | Encrypts GitHub Actions repository secrets using GitHub's sealed box requirement. |

## Usage and integration

GitHubService fetches the repository public key, calls GitHubSecretEncryptor and submits GitHubRepositorySecretRequest.
GitHubRepositorySecretModels also contains the public-key and workflow-dispatch wire shapes.
CloudRepositorySecretBuilder creates values/names; template Models derives the same indexed names.

## Contracts and lifecycle

Encrypt with GitHub's returned public key/sealed-box requirement; do not implement a substitute algorithm. Encoded
ciphertext, plaintext secret values and key material do not belong in logs. Preserve JSON field names and dispatch ref
shape. Encryption for repository upload is separate from at-rest protection of AutoMate metadata and runtime
configuration.

## Related documentation

- [Parent module](../README.md)
- [Upload caller](../Api/README.md)
- [Value builder](../../ApplicationServices/Orchestration/CloudPreparation/README.md)
- [Matching names](../../Templating/Models/README.md)
- [Workflow/secret regressions](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
