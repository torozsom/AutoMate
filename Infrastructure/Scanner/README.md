# Local project scanning

Filesystem discovery, .NET project/dependency metadata and environment-variable extraction.

## Contents

| Module                               | Responsibility                                                 |
|--------------------------------------|----------------------------------------------------------------|
| [Catalog](Catalog/README.md)         | Cached database-provider package rules.                        |
| [Environment](Environment/README.md) | Configuration flattening, .env parsing and extraction.         |
| [Filesystem](Filesystem/README.md)   | Repository/solution discovery and directory exclusions.        |
| [Projects](Projects/README.md)       | .csproj metadata, reference-graph analysis and port selection. |

## Usage and integration

Filesystem finds repositories/solutions; Projects parses .csproj and reference graphs, applies database rules and
selects local ports; Catalog loads package matching rules; Environment reads .NET configuration/.env inputs.
ProjectScannerService and LocalSystemScannerService implement the Application scanning ports.

## Contracts and lifecycle

Scanning describes local inputs, not authorization to access arbitrary paths or assurance of deployment success.
Propagate filesystem/access failures instead of presenting a successful empty configuration scan; malformed optional
JSON retains its existing skip behavior. Extracted configuration may contain secrets. Database package detection is not
provisioning or a promise of cloud-provider support.

## Related documentation

- [Parent module](../README.md)
- [Scanning ports](../../Application/Abstractions/Scanning/README.md)
- [Metadata projections](../../Domain/DTO/Projects/README.md)
- [Deployment consumer](../ApplicationServices/Orchestration/Local/README.md)
