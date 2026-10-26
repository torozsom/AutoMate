# Account resolution

EF-backed identity lookups and connected-Azure metadata behind IUserService.

## Contents

| File                             | Types or asset                     | Responsibility                                                                 |
|----------------------------------|------------------------------------|--------------------------------------------------------------------------------|
| [UserService.cs](UserService.cs) | `UserService`, `RemoteUserDetails` | Implementation of IUserService for managing users using Entity Framework Core. |

## Usage and integration

UserService resolves GitHub/authenticated identifiers to owner IDs, returns existing token/account-kind tuples and
connected Azure projections. Web resolves owners before authorizing project/deployment reads.

## Contracts and lifecycle

Protected conversion yields sensitive plaintext-in-memory tokens; do not log tuples/accounts. Connection presence is not
provider health/current permission. Fresh launch credentials come from AzureArmCredentialsProvider, not this lookup API.
Handle nullable/missing results and forward cancellation.

## Related documentation

- [Parent module](../README.md)
- [Contract](../../../../Application/Data/Users/README.md)
- [Accounts](../../../../Domain/Entities/Users/README.md)
- [Fresh credentials](../../../Azure/Identity/README.md)
- [Consumer](../../../../Web/Routes/Endpoints/DeploymentHistoryEndpoint.cs)
