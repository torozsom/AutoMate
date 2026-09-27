# Email

Email delivery infrastructure adapter implementation.

## Source inventory

- `EmailOptions.cs`
- `GmailSenderService.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
