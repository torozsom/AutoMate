# Bootstrap distribution

Bundled Bootstrap 5.3.3 framework files, as identified by local distribution headers.

## Contents

| Module                 | Responsibility                              |
|------------------------|---------------------------------------------|
| [dist](dist/README.md) | Upstream CSS/JavaScript distribution trees. |

## Usage and integration

dist/css contains full framework/reset/grid/utility variants; dist/js contains standard, ESM and positioning bundles
with maps. App.razor currently references dist/css/bootstrap.min.css. Other variants remain available distributions
rather than automatically active dependencies.

## Contracts and lifecycle

Keep upstream MIT license headers and matching source maps intact. Do not patch minified/generated framework files for
an application styling change; use local CSS/scoped styles. Version here describes the checked-in assets, not the newest
upstream release.

## Related documentation

- [Parent module](../README.md)
- [Loaded variant](../../../Components/App.razor)
- [Local overrides](../../console.css)
- [Current theme tokens](../../obsidian.css)
