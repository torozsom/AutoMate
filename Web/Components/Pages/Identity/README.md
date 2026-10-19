# Page owner resolution

Shared authentication-state to AutoMate owner/account-detail lookup.

## Contents

| File                                                         | Types or asset              | Responsibility                                                                              |
|--------------------------------------------------------------|-----------------------------|---------------------------------------------------------------------------------------------|
| [AuthenticatedUserDetails.cs](AuthenticatedUserDetails.cs)   | `AuthenticatedUserDetails`  | Represents the current AutoMate user details resolved from the active authentication state. |
| [AuthenticatedUserResolver.cs](AuthenticatedUserResolver.cs) | `AuthenticatedUserResolver` | Resolves AutoMate user records from Blazor authentication claims for page components.       |

## Usage and integration

AuthenticatedUserResolver reads the current principal identifier through AuthenticationStateProvider and calls
IUserService. AuthenticatedUserDetails includes owner ID, optional access token and GitHub account classification. Pages
resolve this data inside their independent application-operation scopes.

## Contracts and lifecycle

Claims identify the lookup input, not automatic ownership of a requested project. Downstream owner-scoped ports still
authorize access. Tokens in AuthenticatedUserDetails remain secret even though the type is a small value record; never
log or expose the complete record. Preserve missing/anonymous behavior and scope lifetime.

## Related documentation

- [Parent module](../README.md)
- [Lookup contract](../../../../Application/Data/Users/README.md)
- [Implementation](../../../../Infrastructure/ApplicationServices/Data/Users/README.md)
- [Scoped consumers](../../../Services/README.md)
- [Import callers](../Repositories/README.md)
