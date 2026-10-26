# Bootstrap Icons assets

Bundled Bootstrap Icons 1.11.3 font distribution and MIT license, identified by the local CSS header.

## Contents

| File               | Types or asset | Responsibility                           |
|--------------------|----------------|------------------------------------------|
| [LICENSE](LICENSE) | Asset          | Bundled MIT license for Bootstrap Icons. |

| Module                 | Responsibility                                |
|------------------------|-----------------------------------------------|
| [font](font/README.md) | Icon CSS and referenced binary font payloads. |

## Usage and integration

font/bootstrap-icons.css maps bi-* classes to glyphs and references the relative WOFF/WOFF2 files. App.razor loads this
stylesheet for badges/navigation/actions. LICENSE records the upstream usage terms.

## Contracts and lifecycle

Keep stylesheet, binary fonts and license together. Icon class names are presentation details, not workflow state:
retain visible/accessible labels for meaningful controls and status. Application-specific appearance belongs in local
CSS, not hand-edited vendor glyph maps.

## Related documentation

- [Parent module](../README.md)
- [Icon stylesheet reference](../../../Components/App.razor)
- [Badge consumers](../../../Components/Shared/Projects/README.md)
- [Navigation consumers](../../../Components/Layout/Shell/README.md)
