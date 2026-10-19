# SMTP email adapter

Transactional email delivery through configurable Gmail-compatible SMTP settings.

## Contents

| File                                           | Types or asset       | Responsibility                                                                                |
|------------------------------------------------|----------------------|-----------------------------------------------------------------------------------------------|
| [EmailOptions.cs](EmailOptions.cs)             | `EmailOptions`       | Configuration options for the email sender service.                                           |
| [GmailSenderService.cs](GmailSenderService.cs) | `GmailSenderService` | Sends transactional email through an SMTP provider configured with Gmail-compatible settings. |

## Usage and integration

GmailSenderService implements IEmailSenderService and is consumed by account registration/verification. EmailOptions
controls sender identity, SMTP host/port and app password. The host binds options and registers the sender.

## Contracts and lifecycle

Defaults are smtp.gmail.com:587 and sender name AutoMate. The implementation owns SMTP
connection/authentication/send/disconnect and propagates cancellation. Credentials and verification message/link content
are sensitive; do not log whole messages/options. Send completion is not confirmation the recipient received or read
mail.

## Related documentation

- [Parent module](../README.md)
- [Email port](../../Application/Abstractions/Email/README.md)
- [Verification consumer](../ApplicationServices/Auth/README.md)
- [Authentication workflow tests](../../Test/Infra/Auth/AuthServiceTests.cs)
