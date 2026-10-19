# Bootstrap CSS variants

Upstream full framework, reboot/reset, grid and utility styles, including RTL/minified forms and maps.

## Contents

| File                                                                       | Types or asset | Responsibility                                                                       |
|----------------------------------------------------------------------------|----------------|--------------------------------------------------------------------------------------|
| [bootstrap-grid.css](bootstrap-grid.css)                                   | Asset          | Vendored Bootstrap grid CSS.                                                         |
| [bootstrap-grid.css.map](bootstrap-grid.css.map)                           | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-grid.min.css](bootstrap-grid.min.css)                           | Asset          | Vendored Bootstrap grid CSS; minified distribution.                                  |
| [bootstrap-grid.min.css.map](bootstrap-grid.min.css.map)                   | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-grid.rtl.css](bootstrap-grid.rtl.css)                           | Asset          | Vendored Bootstrap grid CSS for RTL layout.                                          |
| [bootstrap-grid.rtl.css.map](bootstrap-grid.rtl.css.map)                   | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-grid.rtl.min.css](bootstrap-grid.rtl.min.css)                   | Asset          | Vendored Bootstrap grid CSS for RTL layout; minified distribution.                   |
| [bootstrap-grid.rtl.min.css.map](bootstrap-grid.rtl.min.css.map)           | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-reboot.css](bootstrap-reboot.css)                               | Asset          | Vendored Bootstrap reboot/reset CSS.                                                 |
| [bootstrap-reboot.css.map](bootstrap-reboot.css.map)                       | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-reboot.min.css](bootstrap-reboot.min.css)                       | Asset          | Vendored Bootstrap reboot/reset CSS; minified distribution.                          |
| [bootstrap-reboot.min.css.map](bootstrap-reboot.min.css.map)               | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-reboot.rtl.css](bootstrap-reboot.rtl.css)                       | Asset          | Vendored Bootstrap reboot/reset CSS for RTL layout.                                  |
| [bootstrap-reboot.rtl.css.map](bootstrap-reboot.rtl.css.map)               | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-reboot.rtl.min.css](bootstrap-reboot.rtl.min.css)               | Asset          | Vendored Bootstrap reboot/reset CSS for RTL layout; minified distribution.           |
| [bootstrap-reboot.rtl.min.css.map](bootstrap-reboot.rtl.min.css.map)       | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-utilities.css](bootstrap-utilities.css)                         | Asset          | Vendored Bootstrap utility CSS.                                                      |
| [bootstrap-utilities.css.map](bootstrap-utilities.css.map)                 | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-utilities.min.css](bootstrap-utilities.min.css)                 | Asset          | Vendored Bootstrap utility CSS; minified distribution.                               |
| [bootstrap-utilities.min.css.map](bootstrap-utilities.min.css.map)         | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-utilities.rtl.css](bootstrap-utilities.rtl.css)                 | Asset          | Vendored Bootstrap utility CSS for RTL layout.                                       |
| [bootstrap-utilities.rtl.css.map](bootstrap-utilities.rtl.css.map)         | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap-utilities.rtl.min.css](bootstrap-utilities.rtl.min.css)         | Asset          | Vendored Bootstrap utility CSS for RTL layout; minified distribution.                |
| [bootstrap-utilities.rtl.min.css.map](bootstrap-utilities.rtl.min.css.map) | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.css](bootstrap.css)                                             | Asset          | Vendored Bootstrap full framework CSS.                                               |
| [bootstrap.css.map](bootstrap.css.map)                                     | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.min.css](bootstrap.min.css)                                     | Asset          | Vendored Bootstrap full framework CSS; minified distribution.                        |
| [bootstrap.min.css.map](bootstrap.min.css.map)                             | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.rtl.css](bootstrap.rtl.css)                                     | Asset          | Vendored Bootstrap full framework CSS for RTL layout.                                |
| [bootstrap.rtl.css.map](bootstrap.rtl.css.map)                             | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |
| [bootstrap.rtl.min.css](bootstrap.rtl.min.css)                             | Asset          | Vendored Bootstrap full framework CSS for RTL layout; minified distribution.         |
| [bootstrap.rtl.min.css.map](bootstrap.rtl.min.css.map)                     | Asset          | Upstream source map for the matching distribution file; retain alongside its bundle. |

## Usage and integration

App.razor loads bootstrap.min.css as the base framework stylesheet. The remaining variants support alternate
distribution use; they are not all linked at runtime. Application CSS and generated component-scoped styles are loaded
afterward.

## Contracts and lifecycle

Use the matching variant/map pair; loading full, grid and utility distributions together can duplicate/reset rules.
Preserve upstream headers, formatting and RTL artifacts. Application design changes belong in local/scoped CSS, with the
existing stylesheet order retained.

## Related documentation

- [Parent module](../README.md)
- [Active CSS](../../../../../Components/App.razor)
- [Base local styles](../../../../app.css)
- [Shared surfaces](../../../../console.css)
- [Metric styles](../../../../telemetry.css)
