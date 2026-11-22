# Text redaction unit tests

| Suite                                                         | Verifies                                                                          |
|---------------------------------------------------------------|-----------------------------------------------------------------------------------|
| [DiagnosticTextRedactorTests](DiagnosticTextRedactorTests.cs) | Supported credential formats, idempotence, truncation and oversized-input denial. |

Migrated from Test while retaining all assertion scenarios. Parameterized inputs are independently reported
as xUnit theory cases. See the [parent module](../README.md) and
[project boundaries](../../README.md).
