# AI policy tests

| Suite                                                     | Verifies                                                                        |
|-----------------------------------------------------------|---------------------------------------------------------------------------------|
| [AnalysisRetryPolicyTests](AnalysisRetryPolicyTests.cs)   | Retry bounds, jitter ranges, server minimums and exhaustion.                    |
| [AnalysisEgressPolicyTests](AnalysisEgressPolicyTests.cs) | Null approval lists, disallowed routes, explicit regions and missing approvals. |

Migrated from Test while retaining all assertion scenarios. Parameterized inputs are independently reported
as xUnit theory cases. See the [parent module](../README.md) and
[project boundaries](../../README.md).
