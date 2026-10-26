# Synthetic HTTP fixtures

Reusable test clients and callback-driven controlled HTTP responses.

## Contents

| File                                                 | Test or fixture types                                 | Responsibility                                                                                |
|------------------------------------------------------|-------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| [StubHttpClientFactory.cs](StubHttpClientFactory.cs) | `StubHttpClientFactory`, `DelegateHttpMessageHandler` | Shared caller-owned synthetic HTTP clients and callback-driven response handler/JSON builder. |

## Coverage and navigation

StubHttpClientFactory creates clients over a caller-owned handler without transferring disposal ownership.
DelegateHttpMessageHandler invokes a synchronous responder; its Json helper returns an OK JSON response. Provider/Azure
fixtures reuse these helpers without production HTTP.

## Fixtures and boundaries

CreateClient ignores the client name; the handler ignores its cancellation token and propagates responder failures. Do
not infer retry/cancellation/multi-client routing semantics from this simple fake. Callers own handler lifetime and
response-content disposal. Use purpose-built asynchronous handlers when testing real cancellation or body streaming.

## Running

This folder defines helpers rather than a separately discoverable test suite. Run the consuming module filter from its
guide; building Test/Test.csproj compiles these types.

## Related documentation

- [Parent module](../README.md)
- [Provider consumers](../Ai/Providers/README.md)
- [Azure consumers](../Azure/README.md)
- [Port boundaries](../../../Application/Abstractions/README.md)
