# Bootstrap JavaScript variants

Upstream standard scripts, ES modules and positioning bundles with matching source maps.

## Contents

| File                                                       | Types or asset | Responsibility                                                                       |
|------------------------------------------------------------|----------------|--------------------------------------------------------------------------------------|
| [bootstrap.bundle.js](bootstrap.bundle.js)                 | Browser module | Vendored Bootstrap bundle with positioning support.                                  |
| [bootstrap.bundle.js.map](bootstrap.bundle.js.map)         | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.bundle.min.js](bootstrap.bundle.min.js)         | Browser module | Vendored Bootstrap bundle with positioning support; minified distribution.           |
| [bootstrap.bundle.min.js.map](bootstrap.bundle.min.js.map) | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.esm.js](bootstrap.esm.js)                       | Browser module | Vendored Bootstrap ES module.                                                        |
| [bootstrap.esm.js.map](bootstrap.esm.js.map)               | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.esm.min.js](bootstrap.esm.min.js)               | Browser module | Vendored Bootstrap ES module; minified distribution.                                 |
| [bootstrap.esm.min.js.map](bootstrap.esm.min.js.map)       | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.js](bootstrap.js)                               | Browser module | Vendored Bootstrap standard script.                                                  |
| [bootstrap.js.map](bootstrap.js.map)                       | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.min.js](bootstrap.min.js)                       | Browser module | Vendored Bootstrap standard script; minified distribution.                           |
| [bootstrap.min.js.map](bootstrap.min.js.map)               | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |

## Usage and integration

Standard/ESM/bundle variants are alternative distribution entry points; bundle variants include positioning support.
App.razor does not currently load a Bootstrap script, so the presence of these files does not establish live widget
behavior. Existing application interaction belongs to Blazor and first-party modules.

## Contracts and lifecycle

Preserve upstream MIT headers and map/version pairing. Do not load multiple variants together or edit generated bundles
to change application behavior. If a new consumer is introduced, choose its dependency/loading model explicitly and
account for component mount/disposal.

## Related documentation

- [Parent module](../README.md)
- [Current script loading](../../../../../Components/App.razor)
- [Active application behavior](../../../../js/README.md)
- [Navigation consumers](../../../../../Components/Layout/Shell/README.md)
