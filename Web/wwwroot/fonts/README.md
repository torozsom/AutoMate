# Local typefaces

Bundled variable Inter and JetBrains Mono fonts with their SIL Open Font License texts.

## Contents

| File                                                     | Types or asset | Responsibility                                              |
|----------------------------------------------------------|----------------|-------------------------------------------------------------|
| [Inter-OFL.txt](Inter-OFL.txt)                           | Asset          | Bundled SIL Open Font License for Inter.                    |
| [Inter-Variable.ttf](Inter-Variable.ttf)                 | Asset          | Variable Inter font used by application text.               |
| [JetBrainsMono-OFL.txt](JetBrainsMono-OFL.txt)           | Asset          | Bundled SIL Open Font License for JetBrains Mono.           |
| [JetBrainsMono-Variable.ttf](JetBrainsMono-Variable.ttf) | Asset          | Variable JetBrains Mono font used by console/terminal text. |

## Usage and integration

app.css declares Inter for application text and JetBrains Mono for console/terminal text. Browser font URLs resolve
under /fonts; xterm uses the declared family with system fallbacks. The document shell loads app.css before the current
console/theme refinements.

## Contracts and lifecycle

Keep font binaries and the corresponding OFL texts together. Font loading is presentation-only and uses swap/fallback
behavior; do not couple request logic or authorization to font availability. Changes to family names/weights must match
CSS and terminal options. The binaries/licenses are upstream assets; local visual adjustments belong in application
styles.

## Related documentation

- [Parent module](../README.md)
- [Font-face declarations](../app.css)
- [Terminal family](../js/xterm-wrapper.js)
- [Stylesheet loading](../../Components/App.razor)
