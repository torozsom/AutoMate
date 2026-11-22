# Template model naming tests

| Suite                                                         | Methods/cases | Verifies                                                           |
|---------------------------------------------------------------|---------------|--------------------------------------------------------------------|
| [ResourceNameTests](ResourceNameTests.cs)                     | 2/4           | Resource normalization and fallback.                               |
| [DatabaseTypeTests](DatabaseTypeTests.cs)                     | 2/11          | Canonical aliases and trimmed unknown providers.                   |
| [DatabaseLoginTests](DatabaseLoginTests.cs)                   | 1/5           | Login requirements for canonical providers.                        |
| [ResourceSegmentTests](ResourceSegmentTests.cs)               | 2/4           | Preservation within bounds and truncation at separator boundaries. |
| [ContainerAppSecretNameTests](ContainerAppSecretNameTests.cs) | 3/7           | Secret normalization, fallback and the 63-character limit.         |
| [DatabaseSecretNameTests](DatabaseSecretNameTests.cs)         | 2/2           | Indexed username and password secret names.                        |
| [EnvironmentSecretNameTests](EnvironmentSecretNameTests.cs)   | 2/2           | Stable names, normalized-key collisions and bounded hashes.        |

Tests call naming helpers with strings and numbers; no Domain DTOs, model factory or renderer is involved.
Secret-name tests use synthetic keys and assert identity properties rather than reimplementing hashing.
These tests verify naming contracts, not successful cloud provisioning.

See the [production guide](../../../Infrastructure/Templating/Models/README.md) and [parent module](../README.md).
