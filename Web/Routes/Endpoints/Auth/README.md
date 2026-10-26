# Authentication HTTP routes

Local cookie login/logout, GitHub challenge and connected Azure OAuth flows.

## Contents

| File                                             | Types or asset                                                | Responsibility                                                                         |
|--------------------------------------------------|---------------------------------------------------------------|----------------------------------------------------------------------------------------|
| [AzureLoginEndpoint.cs](AzureLoginEndpoint.cs)   | `AzureLoginEndpoint`, `AzureOAuthState`, `AzureTokenResponse` | Endpoint for connecting an Azure account to the current AutoMate user.                 |
| [GitHubLoginEndpoint.cs](GitHubLoginEndpoint.cs) | `GitHubLoginEndpoint`                                         | Endpoint for handling GitHub user login. Initiates the OAuth authentication challenge. |
| [LoginEndpoint.cs](LoginEndpoint.cs)             | `LoginEndpoint`                                               | Endpoint for handling local user login via form post.                                  |
| [LogoutEndpoint.cs](LogoutEndpoint.cs)           | `LogoutEndpoint`                                              | Endpoint for logging out the user. Clears the authentication cookie.                   |

## Usage and integration

LoginEndpoint accepts form credentials and signs the local account into the cookie scheme after IAuthService succeeds.
LogoutEndpoint clears the authenticated cookie. GitHubLoginEndpoint starts provider challenge. AzureLoginEndpoint links
a signed-in AutoMate account, optionally through a tenant-specific distributed-state/code-exchange callback.

## Contracts and lifecycle

Login uses antiforgery-enabled form metadata and local redirects; credentials/errors/account objects never enter fixed
audit fields. Logout requires authentication. Preserve actual endpoint protection per mapping rather than assume every
POST has the same middleware behavior.

Azure tenant-specific state is short-lived (ten minutes), cache-backed and consumed before account linking. Current
account identity is carried from the authorized start. JWT payload extraction is not token validation. Reconnect
requests static user consent for declared ARM/Logs permissions, not tenant-wide admin consent. Keep token scopes,
callback URI/cookie preservation and fixed safe failure handling aligned with host callbacks.

## Route map

| Method/path                        | Responsibility                         |
|------------------------------------|----------------------------------------|
| POST /api/auth/login               | Local credential sign-in.              |
| POST /api/auth/logout              | Authenticated sign-out.                |
| GET /api/auth/github-login         | Public GitHub challenge.               |
| GET /api/auth/azure-login          | Authorized Azure connection start.     |
| GET /api/auth/azure-login/callback | Tenant-specific code/state completion. |

Provider middleware callback paths are configured separately in Hosting.

## Related documentation

- [Parent module](../README.md)
- [Provider ticket handling](../../../Configs/Hosting/OAuthAccountCallbacks.cs)
- [Claim/subscription helpers](../../../Configs/Azure/README.md)
- [Account port](../../../../Application/Auth/README.md)
- [Form UI](../../../Components/Pages/Authentication/README.md)
- [Account tests](../../../../Test/Infra/Auth/AuthServiceTests.cs)
- [OAuth wiring](../../../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
