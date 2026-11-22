# Template path policy tests

| Suite                                                       | Methods/cases | Verifies                                                                     |
|-------------------------------------------------------------|---------------|------------------------------------------------------------------------------|
| [RelativeOutputPathTests](RelativeOutputPathTests.cs)       | 2/2           | Slash normalization and rejection of parent-directory segments.              |
| [OutputPathContainmentTests](OutputPathContainmentTests.cs) | 2/2           | Nested artifacts and rejection of siblings sharing a root-name prefix.       |
| [TemplateRootTests](TemplateRootTests.cs)                   | 2/2           | Repository anchors for .automate outputs and preservation of ordinary roots. |

Paths use platform-native separators. Tests neither create directories nor read template assets.
Containment assertions cover lexical policy, not symbolic-link or filesystem permissions.

See the [production guide](../../../Infrastructure/Templating/Catalog/README.md) and [parent module](../README.md).
