# Console and dialog controls

Bounded terminal hosting, source-aware output formatting, accessible dialogs and theme actions.

## Contents

| File                                                                   | Types or asset                   | Responsibility                                                                                    |
|------------------------------------------------------------------------|----------------------------------|---------------------------------------------------------------------------------------------------|
| [ConsoleDialog.razor](ConsoleDialog.razor)                             | `ConsoleDialog`                  | Hosts accessible dialog content with keyboard focus trapping and disposal-time focus restoration. |
| [DeploymentTerminalPresentation.cs](DeploymentTerminalPresentation.cs) | `DeploymentTerminalPresentation` | Formats Azure records and identifies stderr consistently in live and saved terminal output.       |
| [Terminal.razor](Terminal.razor)                                       | `Terminal`                       | Hosts xterm output with bounded pre-initialization writes and component-owned browser cleanup.    |
| [ThemeToggle.razor](ThemeToggle.razor)                                 | `ThemeToggle`                    | Presents a theme action using the existing browser theme-storage contract.                        |

## Usage and integration

Terminal wraps window.xtermWrapper, queues writes before interactive initialization, emits OnReady and exposes
write/clear methods. DeploymentTerminalPresentation formats Azure records/stderr consistently for live/history.
ConsoleDialog uses console-ui attachDialog for Escape/focus trapping/restoration. ThemeToggle calls the shared theme
storage functions.

## Contracts and lifecycle

Pre-init terminal buffering is bounded and reports overflow. Do not join/replay until terminals render; a fast initial
read must not disappear before JS readiness. Clear replaces output on channel/page changes. Dispose xterm/resize
registrations and pending initialization with component lifetime; avoid content-driven fit loops that grow the viewport.

Presentation prefixes must not rewrite saved message/event/cursor identity. Availability annotations remain separate
operational guidance, not fabricated deployment output. Dialog callers own close actions; dispose registrations/.NET
references and restore focus only when the prior target still exists. Theme uses the single data-bs-theme/localStorage
contract.

## Related documentation

- [Parent module](../README.md)
- [Browser contracts](../../../wwwroot/js/README.md)
- [Replay state](../../../Services/ProjectTerminalReplayState.cs)
- [Terminal owners](../../Pages/Projects/README.md)
- [Viewport/disposal tests](../../../../Test/Web/JavaScript/xterm-wrapper.test.cjs)
- [Formatting tests](../../../../Test/Web/Presentation/DeploymentTerminalPresentationTests.cs)
- [Dialog tests](../../../../Test/Web/Browser/console-ui.test.cjs)
