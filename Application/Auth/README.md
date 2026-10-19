# Authentication contract

Application-facing registration, email verification, local login and connected-provider account linking.

## Contents

| File                               | Types          | Responsibility                                                                            |
|------------------------------------|----------------|-------------------------------------------------------------------------------------------|
| [IAuthService.cs](IAuthService.cs) | `IAuthService` | Service interface for handling user authentication, registration, and email verification. |

## Usage and integration

Call IAuthService from authentication endpoints and account workflows. Infrastructure AuthService implements
persistence, password handling, verification email and provider linkage. Web's OAuth callbacks provide external
identity/token values and host cookie processing.

## Contracts and lifecycle

RegisterAsync takes a verification-link factory; LoginAsync returns a nullable LocalUser and optional error.
VerifyEmailAsync handles verification tokens. GitHub upsert and Azure linking carry sensitive credentials; never log
whole calls or returned accounts. The interface does not issue host cookies, authenticate arbitrary supplied identities
or encrypt in-memory values.

## Related documentation

- [Parent module](../README.md)
- [Authentication implementation](../../Infrastructure/ApplicationServices/Auth/AuthService.cs)
- [OAuth consumers](../../Web/Configs/Hosting/OAuthAccountCallbacks.cs)
- [Account models](../../Domain/Entities/Users/README.md)
- [Authentication tests](../../Test/Infra/Auth/AuthServiceTests.cs)
