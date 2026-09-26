# Auth

`Services/Auth` implements AutoMate's account lifecycle and authentication
domain operations. It creates and updates `Core.Entities.LocalUser` and
`Core.Entities.RemoteUser` records, but it does not own HTTP endpoints,
authentication cookies, OAuth challenges, provider token exchange, or UI
navigation.

The public entry point is `IAuthService`, implemented by `AuthService` and
registered as a scoped service in `Web.Configs.ServiceConfiguration`.

## Responsibilities

- Register local email/password users.
- Hash local passwords through the configured ASP.NET Core password hasher.
- Generate and validate email-verification tokens.
- Authenticate verified local users.
- Rehash passwords when the configured hasher requests it.
- Create or update GitHub-backed `RemoteUser` records during OAuth sign-in.
- Link Microsoft Entra/Azure account data to an existing remote user.
- Persist authentication state through `AutoMateDbContext`.

## Module Boundary

`Services/Auth` owns application-level identity persistence and local
credential rules.

| Concern | Owner |
|---|---|
| `IAuthService` contract and user lifecycle operations | `Services/Auth` |
| Local user and remote user entities | `Core/Entities` |
| Password hashing implementation | ASP.NET `IPasswordHasher<LocalUser>`, registered in `Web` |
| User/token database persistence | `Services.Data.AutoMateDbContext` |
| OAuth authorization challenge and callback endpoints | `Web/Routes/Endpoints/Auth` |
| GitHub/Microsoft provider configuration and claims extraction | `Web/Configs/ServiceConfiguration` |
| Authentication cookie creation/sign-out | `Web/Routes/Endpoints/Auth` |
| Verification email transport | `Services.Email` |
| Verification and login forms | `Web/Components/Pages` |
| OAuth token encryption at rest | `Services.Data` Data Protection converters |

Do not move endpoint redirects, cookie creation, provider SDK objects, or
Blazor concerns into this module.

## Public API

### `IAuthService.RegisterAsync`

Registers a local account:

1. Trims the username and normalizes the email to lowercase invariant form.
2. Rejects empty username, email, or password values.
3. Checks the normalized email against all persisted users.
4. Creates a `LocalUser` with:
   - a hashed password;
   - `IsEmailVerified = false`;
   - a cryptographically random URL-safe verification token;
   - a 24-hour token expiry.
5. Saves the user.
6. Builds a verification link through the caller-supplied
   `verificationLinkFactory`.
7. Sends the verification email through `IEmailSenderService`.

If the database save fails, the method logs the database failure and returns
`false`. If email delivery fails after persistence, it attempts to remove the
new unverified user and returns `false`. This cleanup prevents accounts that
cannot complete verification from remaining in the database.

The link factory belongs to the caller because the URL is presentation and
deployment-host specific. The service supplies only the token.

### `IAuthService.VerifyEmailAsync`

Finds an unverified `LocalUser` by its token and requires that the token has
not expired. A successful verification:

- sets `IsEmailVerified = true`;
- clears `EmailVerificationToken`;
- clears `VerificationTokenExpiry`;
- persists the update.

Tokens are single-use because successful verification removes the token data.
Invalid, expired, already-used, or missing tokens return `false`. Failed-token
logs contain only a short SHA-256 fingerprint, never the token itself.

### `IAuthService.LoginAsync`

Authenticates local accounts by:

1. Normalizing the supplied email.
2. Loading only `LocalUser` records.
3. Returning a generic `"Invalid credentials"` result for empty input,
   unknown users, missing hashes, or an incorrect password.
4. Returning `"Email not verified"` when the password is correct but the
   account has not been verified.
5. Rehashing and saving the password when
   `PasswordVerificationResult.SuccessRehashNeeded` is returned.

The method returns `(LocalUser? User, string? ErrorMessage)` rather than
creating a principal or cookie. `Web.Routes.Endpoints.Auth.LoginEndpoint`
turns a successful user into claims and signs in with the cookie
authentication scheme.

The generic invalid-credentials response intentionally avoids revealing
whether an email address exists.

### `IAuthService.CreateOrUpdateGitHubUserAsync`

Persists the GitHub profile extracted by the OAuth handler:

- `githubId` becomes `RemoteUser.AccountId`;
- username is trimmed, with `"Unknown"` as the fallback;
- email is normalized;
- avatar URL is retained when available;
- the GitHub access token is assigned when provided.

The lookup key is the GitHub account ID, not the email address. Existing
remote users have their GitHub-owned profile fields refreshed; new users are
inserted as `RemoteUser` records.

The service does not initiate OAuth or parse the provider response. GitHub
claim extraction and scope configuration are in `Web`.

### `IAuthService.LinkAzureAccountAsync`

Links Azure/Microsoft identity data to the current remote AutoMate user. The
`currentUserIdentifier` may be either:

- AutoMate's persisted user `Guid`; or
- the GitHub account ID from the current authentication claim.

The service resolves only `RemoteUser` records. On success it updates the
Azure account ID, tenant ID, subscription ID, access token, refresh token, and
expiry timestamp, then saves the entity.

The `email` and `displayName` parameters preserve identity context supplied by
the provider flow, but the current implementation does not overwrite the
remote user's GitHub profile fields with them. Azure token exchange,
subscription discovery, state validation, and callback handling remain in
`Web.Routes.Endpoints.Auth.AzureLoginEndpoint` and the web configuration
extensions.

If the current user cannot be resolved, the method logs a warning and returns
without saving. The interface method returns no success flag; callers should
use the resulting connection state or a subsequent lookup to determine
whether linking succeeded.

## Authentication Flows

### Local registration and login

```text
Web RegistryForm
    -> IAuthService.RegisterAsync
        -> LocalUser + password hash + verification token
        -> AutoMateDbContext
        -> IEmailSenderService
        -> /verify-email?token=...

Web VerifyEmail
    -> IAuthService.VerifyEmailAsync
        -> mark verified and clear token
        -> /login?verified=true

Web LoginEndpoint
    -> IAuthService.LoginAsync
        -> verified LocalUser
        -> cookie claims and SignInAsync
```

The registration form performs user-facing validation such as email format,
username length, password length, and confirmation matching. The service still
normalizes and checks required values because service contracts must not rely
on the UI alone.

### GitHub sign-in

```text
Web /api/auth/github-login
    -> GitHub OAuth challenge
    -> provider callback /signin-github
    -> ProcessGitHubLoginAsync
    -> IAuthService.CreateOrUpdateGitHubUserAsync
    -> GitHub authentication middleware completes the cookie flow
```

GitHub scopes are configured by `Web` and currently include repository,
workflow, and package access needed by later deployment features. Auth should
not add provider scopes; update the provider configuration when permissions
change.

### Azure account connection

There are two web-owned paths:

1. The configured Microsoft OAuth handler uses `/signin-microsoft` and calls
   `LinkAzureAccountAsync` from `ProcessMicrosoftLoginAsync`.
2. The tenant-specific callback uses `/api/auth/azure-login/callback`, validates
   distributed-cache state, exchanges the authorization code for ARM-scoped
   tokens, resolves a subscription, and then calls
   `LinkAzureAccountAsync`.

Both paths attach Azure credentials to an existing remote user. AutoMate does
not create a separate Azure-only user from this flow.

## Security Rules

- Never log passwords, password hashes, verification tokens, access tokens,
  refresh tokens, or full user entities.
- Passwords must always go through `IPasswordHasher<LocalUser>`; do not add
  custom hashing or plaintext persistence.
- Verification tokens must be generated with a cryptographically secure random
  source and must be URL-safe when inserted into links.
- Keep verification-token logs to non-reversible fingerprints.
- Preserve generic invalid-login responses to reduce account enumeration.
- Keep token encryption in `AutoMateDbContext`; `AuthService` receives normal
  plaintext values only while executing the trusted workflow.
- Do not return `RemoteUser` or `LocalUser` directly from HTTP responses.
  Project only the claims or DTO fields required by the caller.
- Do not place OAuth client secrets or provider credentials in this module.
- Pass cancellation tokens through database and email operations.

`Services.Data.AutoMateDbContext` encrypts GitHub and Azure OAuth token
properties through Data Protection value converters before persistence. This
module must not bypass the context with direct SQL or alternate persistence
paths.

## Error and Logging Behavior

Expected user-input failures return ordinary result values:

- registration returns `false`;
- verification returns `false`;
- login returns a user-facing error string;
- Azure linking logs a warning when the current user is absent.

Infrastructure failures are handled according to the operation:

- registration converts `DbUpdateException` into a failed result;
- verification and login allow database/cancellation failures to propagate;
- email delivery failures are logged, trigger cleanup, and return `false`;
- unexpected failures should be logged by the web caller with a safe
  user-facing message.

Avoid adding broad catches that convert database or cancellation failures into
successful-looking authentication results.

## Dependency Injection

`Web.Configs.ServiceConfiguration.RegisterDomainServices` registers:

```csharp
services.AddScoped<IAuthService, AuthService>();
services.AddScoped<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();
```

`AuthService` is scoped because it uses the scoped
`AutoMateDbContext`. `IEmailSenderService` is injected behind its interface.
Keep these dependencies interface-based and do not inject endpoint,
component, or provider-specific types into the service.

## Extension Guidance

When adding an authentication capability:

1. Decide whether it is account lifecycle logic, provider transport, cookie
   handling, or UI behavior.
2. Keep account persistence and normalization in `AuthService`; keep provider
   parsing and OAuth protocol work in `Web`.
3. Add a narrow method to `IAuthService` only when the operation is a stable
   cross-layer contract.
4. Update the corresponding entity, Data Protection configuration, migration,
   and DTO only when the persisted model genuinely changes.
5. Preserve normalized email comparison and the unique email constraint.
6. Review token lifetime, revocation, encryption, logging, and redaction
   before introducing new credential fields.
7. Pass cancellation tokens through all new asynchronous work.
8. Update the local form, endpoint, provider configuration, and documentation
   together when the user-visible flow changes.

For a new external provider, prefer a provider-specific adapter or web
authentication handler that maps into `RemoteUser`. Do not add provider SDK
models or provider-specific branching throughout `AuthService`.

## File Map

| File | Purpose |
|---|---|
| `IAuthService.cs` | Public account lifecycle contract. |
| `AuthService.cs` | Local registration/login/verification and GitHub/Azure user persistence. |

## Related Documentation

- [`Services`](../README.md)
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
