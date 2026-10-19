# Account lookup contract

Authenticated identifier resolution and connected-Azure metadata used by application-facing consumers.

## Contents

| File                               | Types          | Responsibility                                                                                                                      |
|------------------------------------|----------------|-------------------------------------------------------------------------------------------------------------------------------------|
| [IUserService.cs](IUserService.cs) | `IUserService` | Defines operations related to user management and retrieval. Acts as an abstraction layer between the UI and the data access layer. |

## Usage and integration

IUserService maps GitHub account IDs or authenticated identifiers to AutoMate owner IDs, reports whether an account is a
GitHub user and exposes connected Azure credentials. UserService implements the lookups; Web routes use resolved owners
to scope project/deployment operations.

## Contracts and lifecycle

Access-token results and AzureCloudCredentialsDto contain sensitive values. Never log lookup tuples or credentials.
HasAzureConnectionAsync reports connection metadata, not a current provider health or authorization guarantee.
Credential refresh for cloud launch is performed by AzureArmCredentialsProvider; it is separate from this lookup API.
Forward cancellation and handle nullable credential results.

## Related documentation

- [Parent module](../README.md)
- [Lookup implementation](../../../Infrastructure/ApplicationServices/Data/Users/UserService.cs)
- [Fresh ARM credentials](../../../Infrastructure/Azure/Identity/AzureArmCredentialsProvider.cs)
- [Owner resolution consumer](../../../Web/Routes/Endpoints/DeploymentAnalysisEndpoint.cs)
- [Account storage](../../../Domain/Entities/Users/README.md)
- [Credential projection](../../../Domain/DTO/Azure/README.md)
