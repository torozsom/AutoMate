# Project replay-state unit tests

| Suite                                                                 | Verifies                                                                         |
|-----------------------------------------------------------------------|----------------------------------------------------------------------------------|
| [ProjectTerminalReplayStateTests](ProjectTerminalReplayStateTests.cs) | Confirmed/duplicate orders, live-only output, reset and saturated-window replay. |

Migrated from Test while retaining all assertion scenarios. Parameterized inputs are independently reported
as xUnit theory cases. See the [parent module](../README.md) and
[project boundaries](../../README.md).
