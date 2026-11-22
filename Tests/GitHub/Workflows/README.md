# GitHub log unit tests

| Suite                                                                   | Verifies                                                                        |
|-------------------------------------------------------------------------|---------------------------------------------------------------------------------|
| [GitHubWorkflowLogNormalizerTests](GitHubWorkflowLogNormalizerTests.cs) | Terminal escape removal, bounded lines and continuation without dropped output. |

Migrated from Test while retaining all assertion scenarios. Parameterized inputs are independently reported
as xUnit theory cases. See the [parent module](../README.md) and
[project boundaries](../../README.md).
