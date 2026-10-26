# Bundled browser libraries

Upstream Bootstrap framework and Bootstrap Icons distributions used by Web presentation.

## Contents

| Module                                       | Responsibility                                 |
|----------------------------------------------|------------------------------------------------|
| [bootstrap](bootstrap/README.md)             | Bundled Bootstrap framework files.             |
| [bootstrap-icons](bootstrap-icons/README.md) | Bootstrap Icons license and font distribution. |

## Usage and integration

App.razor loads the Bootstrap CSS entry point and Bootstrap Icons font stylesheet. Follow each child guide for variants,
relative paths and license provenance. First-party behavior/styles live outside this vendor tree.

## Contracts and lifecycle

Preserve upstream minification, maps, font URLs, license headers and the Icons license file. The presence of a
JavaScript distribution does not mean App currently loads it. Keep library upgrades deliberate and verify associated
CSS/font/script variants together; documentation changes do not alter dependency versions.

## Related documentation

- [Parent module](../README.md)
- [Actual entry points](../../Components/App.razor)
- [Application behavior](../js/README.md)
- [Asset order](../README.md)
