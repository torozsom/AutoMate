# Browser assets

Global styles, first-party JavaScript, locally served fonts, iconography and vendored framework distributions.

## Contents

| File                           | Types or asset | Responsibility                                                                |
|--------------------------------|----------------|-------------------------------------------------------------------------------|
| [app.css](app.css)             | Asset          | Local font faces, base styles and shared light/dark presentation.             |
| [console.css](console.css)     | Asset          | Shared workspace/console surfaces, layout and reusable control styling.       |
| [favicon.png](favicon.png)     | Asset          | Browser tab/site icon referenced by the document shell.                       |
| [obsidian.css](obsidian.css)   | Asset          | Current visual tokens and responsive project/workspace/telemetry refinements. |
| [telemetry.css](telemetry.css) | Asset          | Historical metrics, chart/table and shared telemetry presentation.            |

| Module                   | Responsibility                                              |
|--------------------------|-------------------------------------------------------------|
| [fonts](fonts/README.md) | Locally served variable typefaces and licenses.             |
| [js](js/README.md)       | First-party terminal/chart/dialog/navigation/theme modules. |
| [lib](lib/README.md)     | Vendored Bootstrap distributions and icon assets.           |

## Usage and integration

App.razor loads Bootstrap/icon CSS, app.css, generated scoped Web.styles.css, telemetry.css, console.css and
obsidian.css in that order. theme.js initializes the saved theme before first paint; terminal libraries/wrapper load for
the shared console. Child guides describe module consumers and lifetime responsibilities.

## Contracts and lifecycle

Static files are public presentation resources: no credentials/private runtime data belong here. Theme tokens and local
fonts are shared by live/history views. Component-scoped styles remain beside Razor files and are built into generated
CSS, not hand-edited here.

Vendored Bootstrap/icon files retain upstream headers/maps/licenses; first-party behavior belongs in local
modules/styles. xterm and its Fit addon currently load pinned CDN assets from App.razor, so local fonts/Bootstrap do not
make the entire page offline. Preserve browser callback names and dispose listeners/observers/terminals with their
owning components.

## Related documentation

- [Parent module](../README.md)
- [Asset entry point](../Components/App.razor)
- [Terminal/dialog consumers](../Components/Shared/Console/README.md)
- [Chart consumers](../Components/Shared/Metrics/README.md)
- [Browser checks](../../Test/Web/Browser/README.md)
