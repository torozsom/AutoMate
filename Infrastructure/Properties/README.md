# Assembly properties

Assembly-level metadata that supports internal adapter regression coverage.

## Contents

| File                               | Types or asset      | Responsibility                                                                     |
|------------------------------------|---------------------|------------------------------------------------------------------------------------|
| [AssemblyInfo.cs](AssemblyInfo.cs) | Assembly attributes | Grants the Test assembly access to internal adapters without widening public APIs. |

## Usage and integration

AssemblyInfo grants InternalsVisibleTo to the Test assembly. Tests can exercise internal adapters without making
implementation helpers public or changing Application contracts.

## Contracts and lifecycle

Friend-assembly access is compile-time visibility, not runtime authorization. Keep the exact assembly name aligned with
the test project; do not expose production APIs merely to satisfy tests. This folder has no runtime service registration
or provider configuration.

## Related documentation

- [Parent module](../README.md)
- [Friend assembly](../../Test/Test.csproj)
- [Assembly project](../Infrastructure.csproj)
- [Adapter tests](../../Test/Infra/README.md)
