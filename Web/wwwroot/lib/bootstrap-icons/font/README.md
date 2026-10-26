# Icon font mapping

Bootstrap Icons class/glyph CSS and its binary font directory.

## Contents

| File                                       | Types or asset | Responsibility                                                 |
|--------------------------------------------|----------------|----------------------------------------------------------------|
| [bootstrap-icons.css](bootstrap-icons.css) | Asset          | Vendored icon class/glyph mapping and relative font-face URLs. |

| Module                   | Responsibility                 |
|--------------------------|--------------------------------|
| [fonts](fonts/README.md) | WOFF/WOFF2 icon font binaries. |

## Usage and integration

bootstrap-icons.css declares @font-face and bi-* pseudo-element mappings. Its URLs are relative to this folder's fonts
child. App.razor loads this file; consumers use existing bi classes in Razor markup.

## Contracts and lifecycle

Preserve relative font URLs, query suffixes, class mappings and upstream license header. Do not treat the minified
mapping as first-party editable CSS. Glyph-only decoration must not replace accessible text for controls or distinct
status semantics.

## Related documentation

- [Parent module](../README.md)
- [License](../LICENSE)
- [CSS loader](../../../../Components/App.razor)
- [Example consumer](../../../../Components/Shared/Projects/DeploymentBadge.razor)
