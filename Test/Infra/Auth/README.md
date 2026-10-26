# Authentication service tests

Registration compensation, verification timing and local account behavior at the real service boundary.

## Contents

| File                                       | Test or fixture types                | Responsibility                                                                                                 |
|--------------------------------------------|--------------------------------------|----------------------------------------------------------------------------------------------------------------|
| [AuthServiceTests.cs](AuthServiceTests.cs) | `AuthServiceTests`, `Email`, `Clock` | Characterizes registration compensation, verification boundaries and local login at the real service boundary. |

## Coverage and navigation

AuthServiceTests calls the production AuthService using owned relational metadata, controlled email delivery and
explicit time. It characterizes compensation on delivery failure and verification expiry boundaries while preserving
local login/account semantics.

## Fixtures and boundaries

No real SMTP/provider account is used. Test credentials/tokens remain synthetic. Preserve constructor/time seams and
existing compensation rather than assert a made-up stronger transactional email guarantee. HTTP cookies/OAuth mapping
are Web concerns tested separately.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Auth
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Implementation](../../../Infrastructure/ApplicationServices/Auth/README.md)
- [Contract](../../../Application/Auth/README.md)
- [Real login/audit HTTP cases](../../Web/Diagnostics/OperationalLoggingTests.cs)
