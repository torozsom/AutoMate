# Authentication persistence

Local registration/login/email verification and GitHub/Azure linkage behind IAuthService.

## Contents

| File                             | Types or asset                                                                | Responsibility                                                                                 |
|----------------------------------|-------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------|
| [AuthService.cs](AuthService.cs) | `AuthService`, `RegistrationInput`, `GitHubProfile`, `AzureAccountConnection` | Service implementation for handling user authentication, registration, and email verification. |

## Usage and integration

AuthService coordinates AutoMateDbContext, supported password hashing and IEmailSenderService. Web endpoints/OAuth
callbacks supply identity data and verification-link generation; cookie processing remains in the host. Clock-aware
constructors support deterministic expiry tests while original constructors retain TimeProvider.System.

## Contracts and lifecycle

Passwords are hashed, not stored as plaintext. Verification uses stored token/expiry metadata; registration mail failure
follows existing compensation behavior. GitHub/Azure credentials use protected mappings but remain sensitive in memory.
Log internal GUIDs and finite outcomes, not names/emails/tokens. Propagate cancellation without changing compensation or
expiry boundaries.

## Related documentation

- [Parent module](../README.md)
- [Contract](../../../Application/Auth/README.md)
- [Accounts](../../../Domain/Entities/Users/README.md)
- [Mail transport](../../Email/README.md)
- [Tests](../../../Test/Infra/Auth/AuthServiceTests.cs)
