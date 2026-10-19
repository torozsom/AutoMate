# Local repository discovery

Directory exclusion rules and asynchronous repository/solution/C# project discovery.

## Contents

| File                                                           | Types or asset               | Responsibility                                                                            |
|----------------------------------------------------------------|------------------------------|-------------------------------------------------------------------------------------------|
| [LocalScannerDirectoryRules.cs](LocalScannerDirectoryRules.cs) | `LocalScannerDirectoryRules` | Centralizes filesystem rules used by local project discovery.                             |
| [LocalSystemScannerService.cs](LocalSystemScannerService.cs)   | `LocalSystemScannerService`  | Scans local directories for Git repositories that contain .NET solution or project files. |

## Usage and integration

LocalSystemScannerService implements ILocalSystemScannerService using LocalScannerDirectoryRules and
LocalCsProjectParser. It locates local repositories with .NET content and resolves a solution root for a selected
project. Results use LocalProjectDto/CsProjectDto rather than persistence entities.

## Contracts and lifecycle

Respect exclusions and the implementation's solution/repository traversal boundaries; do not treat names alone as trust
or filesystem permission. Discovery is descriptive and does not build/execute found projects. Propagate cancellation and
preserve existing inaccessible/malformed input behavior. Returned absolute paths are operational data, not safe metric
dimensions.

## Related documentation

- [Parent module](../README.md)
- [Discovery port](../../../Application/Abstractions/Scanning/README.md)
- [Project parsing](../Projects/README.md)
- [Discovery outputs](../../../Domain/DTO/Projects/README.md)
- [Solution-root consumer](../../ApplicationServices/Orchestration/Local/README.md)
