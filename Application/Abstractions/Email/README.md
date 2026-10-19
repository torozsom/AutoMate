# Email delivery port

A transport-neutral asynchronous email-sending interface.

## Contents

| File                                             | Types                 | Responsibility                               |
|--------------------------------------------------|-----------------------|----------------------------------------------|
| [IEmailSenderService.cs](IEmailSenderService.cs) | `IEmailSenderService` | Interface for sending emails asynchronously. |

## Usage and integration

IEmailSenderService accepts recipient, subject and message with cancellation. AuthService uses it for
account-verification mail; GmailSenderService is the current Infrastructure implementation. Provider credentials and
SMTP/client configuration belong to Infrastructure.

## Contracts and lifecycle

Completion represents the sender operation, not proof the recipient read or received the email. Propagate cancellation
and let the caller handle delivery failure. Messages can include verification links/tokens and account details; avoid
logging their full contents.

## Related documentation

- [Parent module](../README.md)
- [Current sender](../../../Infrastructure/Email/GmailSenderService.cs)
- [Email configuration](../../../Infrastructure/Email/README.md)
- [Verification-mail consumer](../../../Infrastructure/ApplicationServices/Auth/AuthService.cs)
- [Authentication boundary](../../Auth/README.md)
