# Email

`Services/Email` is AutoMate's transactional email adapter. It exposes a
small interface for sending messages and currently implements that interface
with a Gmail-compatible SMTP sender built on MailKit.

The module is intentionally narrow. It does not create verification tokens,
persist users, build authentication cookies, or own registration workflow
rollback. `Services.Auth` supplies the message content and decides what a
delivery failure means for account registration.

## Responsibilities

- Define the `IEmailSenderService` application-facing contract.
- Bind SMTP settings through the Options pattern.
- Validate required recipient, subject, and credential values.
- Build a `MimeMessage` with configured sender information.
- Connect to the SMTP provider using StartTLS.
- Authenticate with the configured sender address and app password.
- Send the message asynchronously with cancellation support.
- Disconnect the SMTP client in a `finally` block.

## Module Structure

```text
Services/Email/
├── IEmailSenderService.cs
├── GmailSenderService.cs
└── EmailOptions.cs
```

## Public Contract

`IEmailSenderService.SendEmailAsync` accepts:

| Parameter | Meaning |
|---|---|
| `toEmail` | Recipient mailbox address. |
| `subject` | Message subject. |
| `message` | Message body supplied by the caller. |
| `cancellationToken` | Cancels SMTP connection, authentication, or send operations. |

The interface is provider-neutral even though the current implementation is
named `GmailSenderService`. Callers should depend on the interface rather than
MailKit, `SmtpClient`, or `EmailOptions`.

The interface documentation describes the body as HTML, but the current
implementation assigns the value to `BodyBuilder.TextBody`. The message is
therefore sent as plain text today. If HTML delivery is required, update the
MIME construction deliberately and consider whether a plain-text alternative
should be included for clients that cannot render HTML.

## Current Delivery Flow

```text
AuthService.RegisterAsync
    -> create and persist unverified LocalUser
    -> build verification link
    -> IEmailSenderService.SendEmailAsync
        -> validate recipient and subject
        -> validate sender credentials
        -> create MimeMessage
        -> connect with SMTP StartTLS
        -> authenticate with sender/app password
        -> send message
        -> disconnect
    -> registration succeeds
```

`AuthService` sends the registration verification subject and body. If the
email operation throws, Auth logs the failure, attempts to remove the newly
created unverified user, and returns a failed registration result. Email
delivery therefore participates in registration success, but the Email module
does not know about users or verification.

## `GmailSenderService`

The implementation receives `IOptions<EmailOptions>` and
`ILogger<GmailSenderService>` through dependency injection.

### Validation

`SendEmailAsync` throws `ArgumentException` when the recipient or subject is
blank. It throws `InvalidOperationException` when either `SenderEmail` or
`AppPassword` is missing. These are configuration or contract failures and
should not be converted into a successful-looking send result.

The recipient is passed to `MailboxAddress.Parse`, so malformed mailbox
syntax can also fail while the MIME message is being created. Provider and
network errors from MailKit are allowed to propagate to the caller.

### SMTP session

Each send creates a new `MailKit.Net.Smtp.SmtpClient` and:

1. connects to `SmtpHost` and `SmtpPort`;
2. requests `SecureSocketOptions.StartTls`;
3. authenticates with `SenderEmail` and `AppPassword`;
4. sends the generated MIME message;
5. disconnects with a clean SMTP quit when connected.

The client is disposed after the operation. Disconnect uses
`CancellationToken.None` in the cleanup path so an already-cancelled send
still attempts to close the connection.

### MIME message

The message contains:

- `From`: `SenderName` and `SenderEmail`;
- `To`: the parsed recipient address;
- `Subject`: the caller-provided subject;
- `TextBody`: the caller-provided message.

The sender address is configuration-controlled. Callers should not be able to
override it through a general-purpose UI field unless that behavior is
explicitly designed and validated.

## Configuration

`EmailOptions.SectionName` is `Email`. `Web.Configs.ServiceConfiguration`
binds that section:

```csharp
builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName));
```

Available settings:

| Setting | Default | Purpose |
|---|---|---|
| `SenderEmail` | empty | SMTP username and outgoing sender address. |
| `AppPassword` | empty | SMTP app password/provider credential. |
| `SenderName` | `AutoMate` | Display name in the `From` header. |
| `SmtpHost` | `smtp.gmail.com` | SMTP server hostname. |
| `SmtpPort` | `587` | SMTP StartTLS port. |

The current registration uses the default `GmailSenderService`:

```csharp
services.AddScoped<IEmailSenderService, GmailSenderService>();
```

The options section can be supplied by `appsettings`, environment variables,
user-secrets, or another standard ASP.NET Core configuration provider. For
local development, prefer user-secrets or environment variables for
`Email:AppPassword` and other credentials. Never commit real passwords,
tokens, API keys, or SMTP credentials to source control.

If the provider changes, update the SMTP host, port, and authentication
requirements together. Do not assume every provider supports Gmail-style app
passwords or StartTLS on port 587.

## Boundaries

| Concern | Owner |
|---|---|
| SMTP transport and MIME construction | `Services/Email` |
| Email options binding | `Web.Configs.ServiceConfiguration` |
| Verification-token generation and expiry | `Services/Auth` |
| Verification-link URL construction | Registration caller in `Web` |
| User persistence and cleanup after send failure | `Services.Auth` and `Services.Data` |
| Registration form and user-facing errors | `Web` |
| Credential storage mechanism | ASP.NET Core configuration providers |

Do not add database access, `LocalUser` dependencies, endpoint redirects,
Blazor component references, or authentication-cookie logic to this module.

## Error, Logging, and Cancellation Rules

- Pass the caller's cancellation token to every MailKit operation.
- Let cancellation propagate as cancellation; do not turn it into a
  successful send.
- Keep logs useful for operations but never log app passwords, recipient
  credentials, or complete message bodies when they may contain tokens.
- The current implementation logs the subject and generic success/failure
  events, but not credentials.
- Preserve the `finally` disconnect behavior when changing the SMTP flow.
- Do not add a broad catch in the sender that hides provider failures. The
  caller owns workflow-specific recovery.

The registration flow currently catches failures around the email call because
it must remove the persisted unverified account when delivery fails. Other
future callers may need different recovery behavior, so the sender should
remain explicit about failures.

## Testing and Local Verification

Because the implementation talks to an external SMTP server, tests should
prefer replacing `IEmailSenderService` with a fake or test double. Verify:

- valid input reaches the expected caller-visible success path;
- blank recipient and subject values are rejected;
- missing credentials fail before an SMTP connection is attempted;
- cancellation is passed through;
- SMTP failures propagate to the caller;
- the SMTP client is disconnected after both successful and failed sends;
- registration cleanup is triggered by `AuthService` when delivery fails.

Avoid tests that send real messages from the default test suite. Use a
provider sandbox or a local SMTP test server for explicit integration tests.

## Extending the Module

When adding a new email capability:

1. Decide whether it belongs in the transport adapter or in the owning
   application workflow.
2. Keep the interface focused on stable delivery operations.
3. Keep token generation, link construction, persistence, and user lifecycle
   outside this module.
4. Add a provider-specific implementation rather than branching provider
   details throughout callers.
5. Keep provider credentials in configuration and validate required settings
   before opening a connection.
6. Preserve cancellation and cleanup behavior.
7. Decide explicitly whether the body is plain text, HTML, or multipart.
8. Add tests using a fake sender or controlled SMTP test server.

If multiple providers are supported, register the selected implementation in
`Web.Configs.ServiceConfiguration` and keep callers dependent only on
`IEmailSenderService`.

## File Map

| File | Purpose |
|---|---|
| `IEmailSenderService.cs` | Provider-neutral asynchronous email contract. |
| `GmailSenderService.cs` | MailKit SMTP implementation using StartTLS and configured credentials. |
| `EmailOptions.cs` | Strongly typed `Email` configuration model. |

## Related Documentation

- [`Services`](../README.md)
- [`Services/Auth`](../Auth/README.md)
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
