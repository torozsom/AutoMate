# Error presentation

Fixed error/missing-page guidance and optional request correlation.

## Contents

| File                             | Types or asset | Responsibility                                                                |
|----------------------------------|----------------|-------------------------------------------------------------------------------|
| [Error.razor](Error.razor)       | `Error`        | Displays fixed error guidance and an optional request correlation identifier. |
| [Error.razor.cs](Error.razor.cs) | `Error`        | Represents an error page component in a Blazor application.                   |
| [NotFound.razor](NotFound.razor) | `NotFound`     | Displays the missing-page view and a navigation path back to the application. |

## Usage and integration

Error maps /Error for host exception handling and derives a request ID from activity/HttpContext. NotFound maps
/not-found and is the Router's missing-page component. MainLayout provides navigation back into the application.

## Contracts and lifecycle

Show a correlation identifier when available, not an exception message/stack/configuration dump. Host middleware owns
exception/status-code dispatch; the page is not a recovery or logging backend. Missing routes and failed operations must
retain distinct guidance.

## Related documentation

- [Parent module](../README.md)
- [Error/status middleware](../../../Configs/Hosting/AppConfiguration.cs)
- [NotFoundPage mapping](../../Routes.razor)
- [Safe operation audit](../../../Observability/README.md)
