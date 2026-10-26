# Authentication pages

Public local login/registration and email-verification presentation.

## Contents

| File                                             | Types or asset                  | Responsibility                                                                                                                                                                             |
|--------------------------------------------------|---------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [LoginForm.razor](LoginForm.razor)               | `LoginForm`                     | Presents local credential login and provider sign-in with query-based operation feedback.                                                                                                  |
| [LoginForm.razor.cs](LoginForm.razor.cs)         | `LoginForm`                     | Represents the login form component used for user authentication. Handles standard credentials and displays status messages for registration and verification.                             |
| [LoginForm.razor.css](LoginForm.razor.css)       | Scoped stylesheet               | Scoped styling for LoginForm.                                                                                                                                                              |
| [RegistryForm.razor](RegistryForm.razor)         | `RegistryForm`                  | Collects validated registration details and submits account creation with email verification.                                                                                              |
| [RegistryForm.razor.cs](RegistryForm.razor.cs)   | `RegistryForm`, `RegisterModel` | The RegistryForm component is responsible for handling the registration process for new users. It displays a form for user input, validates the input, and handles the registration logic. |
| [RegistryForm.razor.css](RegistryForm.razor.css) | Scoped stylesheet               | Scoped styling for RegistryForm.                                                                                                                                                           |
| [VerifyEmail.razor](VerifyEmail.razor)           | `VerifyEmail`                   | Presents email verification status from the supplied confirmation token.                                                                                                                   |
| [VerifyEmail.razor.cs](VerifyEmail.razor.cs)     | `VerifyEmail`                   | This component handles the email verification process for users. It either prompts the user to check their email or validates a provided token.                                            |

## Usage and integration

LoginForm maps /login and submits the existing HTTP cookie-login endpoint. RegistryForm maps /register and uses
ScopedApplicationOperations for IAuthService registration with verification-link generation. VerifyEmail maps
/verify-email and processes token/checkemail query state. Scoped CSS remains beside each form.

## Contracts and lifecycle

RegisterModel applies form validation/submission guards; server auth remains authoritative. Login query flags present
fixed registration/verification feedback. Auth calls use independent scopes rather than a shared circuit DbContext.
Tokens/passwords and returned accounts are sensitive; never render raw provider failure text or log complete form
models. Preserve existing navigation and registration compensation behavior.

## Related documentation

- [Parent module](../README.md)
- [HTTP login/cookies](../../../Routes/Endpoints/Auth/README.md)
- [Auth contract](../../../../Application/Auth/README.md)
- [Implementation](../../../../Infrastructure/ApplicationServices/Auth/README.md)
- [Scope boundary](../../../Services/ScopedApplicationOperations.cs)
- [Auth regressions](../../../../Test/Infra/Auth/AuthServiceTests.cs)
