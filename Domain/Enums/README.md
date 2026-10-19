# Shared classifications

Finite values used by project metadata, deployment/cloud execution, AI analysis and GitHub diagnostic retrieval.

## Contents

| Module                               | Responsibility                                          |
|--------------------------------------|---------------------------------------------------------|
| [Ai](Ai/README.md)                   | Analysis lifecycle and admission trigger.               |
| [Deployments](Deployments/README.md) | Runtime status, completion outcome and cloud-run phase. |
| [GitHub](GitHub/README.md)           | Job-log retrieval availability.                         |
| [Projects](Projects/README.md)       | Application kind and source location.                   |

## Usage and integration

Use the classification for the relevant workflow dimension. Enums label recorded state; application and infrastructure
consumers own transitions and provider-status translation. Child guides link concrete consumers and relevant regression
coverage.

## Compatibility

Preserve numeric values/order where persisted numerically and names where persisted as strings. There is no single
representation for all enums: CloudRunPhase uses a string EF conversion
in [model configuration](../../Infrastructure/Data/AutoMateModelConfiguration.cs). Review admission, workers, readers
and presentation when adding states.

## Related documentation

- [Domain](../README.md)
- [Entity consumers](../Entities/README.md)
- [DTO consumers](../DTO/README.md)
