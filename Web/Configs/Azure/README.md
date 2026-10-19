# Azure OAuth helpers

Subscription discovery and selected JWT payload extraction for connected-account setup.

## Contents

| File                                                         | Types or asset              | Responsibility                                                |
|--------------------------------------------------------------|-----------------------------|---------------------------------------------------------------|
| [AzureSubscriptionResolver.cs](AzureSubscriptionResolver.cs) | `AzureSubscriptionResolver` | Resolves Azure subscription IDs for connected Azure accounts. |
| [JwtPayloadReader.cs](JwtPayloadReader.cs)                   | `JwtPayloadReader`          | Reads values from JWT payloads without validating the token.  |

## Usage and integration

AzureSubscriptionResolver queries ARM subscriptions using the exchanged access token. JwtPayloadReader extracts selected
string claims for OAuth callbacks/tenant-specific flow. Hosting OAuthAccountCallbacks and AzureLoginEndpoint own the
surrounding account-link/cookie/state workflow.

## Contracts and lifecycle

JwtPayloadReader decodes payload JSON without validating signature, issuer, audience or expiry. It must not be used as
independent authentication/authorization. Invalid encoding/JSON returns null for the handled cases; other documented
shape failures can propagate. Subscription lookup supplies connection metadata, not proof of authorization for every
resource. Tokens/claim payloads must not be logged.

## Related documentation

- [Parent module](../README.md)
- [Ticket consumer](../Hosting/OAuthAccountCallbacks.cs)
- [Tenant-specific flow](../../Routes/Endpoints/Auth/AzureLoginEndpoint.cs)
- [Execution-time credentials](../../../Infrastructure/Azure/Identity/README.md)
- [OAuth composition tests](../../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
