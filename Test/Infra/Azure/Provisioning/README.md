# Azure provisioning tests

Fresh ARM credentials, required-provider readiness and safe failure/deadline behavior.

## Contents

| File                                                                             | Test or fixture types                 | Responsibility                                                                                            |
|----------------------------------------------------------------------------------|---------------------------------------|-----------------------------------------------------------------------------------------------------------|
| [AzureArmCredentialsProviderTests.cs](AzureArmCredentialsProviderTests.cs)       | `AzureArmCredentialsProviderTests`    | Verifies launches obtain current ARM credentials instead of reusing an expired login token.               |
| [AzureResourceProviderRegistrarTests.cs](AzureResourceProviderRegistrarTests.cs) | `AzureResourceProviderRegistrarTests` | Ensures diagnostics provider readiness precedes deployment and safe failures do not leak response bodies. |

## Coverage and navigation

AzureArmCredentialsProviderTests verifies launch refresh rather than reusing an expired login token and handles
revocation. AzureResourceProviderRegistrarTests verifies Microsoft.Insights registration precedes readiness, masks
denied responses/credentials and observes stalled-registration deadlines.

## Fixtures and boundaries

Use controlled OAuth/ARM responses and owned metadata, never live resource-group/identity/RBAC changes. Preserve request
ordering, cancellation and finite safe guidance. A successful synthetic readiness response does not certify actual
tenant permissions or propagation time.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Azure.Provisioning
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Credential refresh](../../../../Infrastructure/Azure/Identity/README.md)
- [Provider registration](../../../../Infrastructure/Azure/Provisioning/README.md)
- [Generated Azure configuration](../../Templating/README.md)
