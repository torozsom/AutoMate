# Identity and baseline schema

Initial metadata schema plus remote-account/Azure connection evolution.

## Contents

| File                                                                                           | Types or asset       | Responsibility                                                          |
|------------------------------------------------------------------------------------------------|----------------------|-------------------------------------------------------------------------|
| [20260522134720_Initial.cs](20260522134720_Initial.cs)                                         | `Initial`            | EF schema migration and target-model metadata for Initial.              |
| [20260522134720_Initial.Designer.cs](20260522134720_Initial.Designer.cs)                       | `Initial`            | EF schema migration and target-model metadata for Initial.              |
| [20260524171414_RefactorRemoteUser.cs](20260524171414_RefactorRemoteUser.cs)                   | `RefactorRemoteUser` | EF schema migration and target-model metadata for Refactor Remote User. |
| [20260524171414_RefactorRemoteUser.Designer.cs](20260524171414_RefactorRemoteUser.Designer.cs) | `RefactorRemoteUser` | EF schema migration and target-model metadata for Refactor Remote User. |

## Usage and integration

Initial establishes accounts, applications, C# projects, configuration and deployments. RefactorRemoteUser changes
remote identity fields and adds Azure connection/token/expiry metadata. Each main file is paired with its generated
target-model designer.

## Contracts and lifecycle

EF orders these globally by migration ID, not this folder. Historical schema is not the current runtime mapping; compare
Data and later migrations for present behavior. Preserve discriminator, column and migration identity compatibility.
Later protected conversion/key configuration governs token storage access; adding columns is not itself encryption or
authorization. Down removes schema/data and cannot promise restoration.

## Related documentation

- [Parent module](../README.md)
- [Runtime mappings](../../Data/README.md)
- [Current accounts](../../../Domain/Entities/Users/README.md)
- [Project ownership](../../../Domain/Entities/Projects/README.md)
- [Account regressions](../../../Test/Infra/Auth/AuthServiceTests.cs)
- [Migration-chain coverage](../../../Test/Infra/Ai/Admission/AnalysisBudgetPostgresTests.cs)
