# Bootstrap runtime variants

CSS and JavaScript files retained in the upstream distribution layout.

## Contents

| Module               | Responsibility                                           |
|----------------------|----------------------------------------------------------|
| [css](css/README.md) | Bootstrap CSS variants and corresponding source maps.    |
| [js](js/README.md)   | Bootstrap script/module/bundle variants and source maps. |

## Usage and integration

Choose an entry from css/js only when the document's actual consumers need it. Full CSS is already loaded by App;
JavaScript files are bundled assets and are not loaded merely because the server serves this folder. Child indexes
enumerate every distribution and map.

## Contracts and lifecycle

Preserve relative paths and version consistency across minified/nonminified/ESM/bundle variants. Source maps support
debugging and are not separate runtime behavior. Application controls use local Blazor/JS behavior; adding a framework
script must not accidentally duplicate its listeners.

## Related documentation

- [Parent module](../README.md)
- [Document references](../../../../Components/App.razor)
- [Current first-party modules](../../../js/README.md)
