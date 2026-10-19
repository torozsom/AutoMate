# Icon font binaries

WOFF and WOFF2 glyph payloads referenced by the parent Bootstrap Icons stylesheet.

## Contents

| File                                           | Types or asset | Responsibility                                             |
|------------------------------------------------|----------------|------------------------------------------------------------|
| [bootstrap-icons.woff](bootstrap-icons.woff)   | Asset          | Bootstrap Icons font payload referenced by the parent CSS. |
| [bootstrap-icons.woff2](bootstrap-icons.woff2) | Asset          | Bootstrap Icons font payload referenced by the parent CSS. |

## Usage and integration

The browser selects a supported format through bootstrap-icons.css. Both files are distribution assets for the same icon
class/glyph mapping; application code uses CSS classes rather than loading or parsing the binaries.

## Contracts and lifecycle

Keep filenames/paths aligned with parent CSS and retain the bundled MIT license. These files provide presentation glyphs
only; there are no application services or extension APIs in this folder. Preserve upstream binary contents during
documentation/style work.

## Related documentation

- [Parent module](../README.md)
- [Font-face URLs](../bootstrap-icons.css)
- [License](../../LICENSE)
- [Icon consumers](../../../../../Components/Shared/Projects/README.md)
