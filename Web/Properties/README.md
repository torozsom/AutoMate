# Local launch profiles

IDE Development launch configuration for Web.

## Contents

| File                                       | Types or asset | Responsibility                                                     |
|--------------------------------------------|----------------|--------------------------------------------------------------------|
| [launchSettings.json](launchSettings.json) | Configuration  | IDE http/https Development profiles and local listening addresses. |

## Usage and integration

launchSettings.json defines http and https Project profiles. The https profile uses localhost ports 7288 (HTTPS) and
5271 (HTTP); both select Development. Use the repository's IDE deployment workflow to start supporting services before
launching Web.

## Contracts and lifecycle

These profiles are local tooling defaults, not production hosting/OAuth secrets. App settings/user secrets/environment
configuration supply service connections. Keep callback/listening addresses consistent with the selected local profile.
Do not copy machine-specific credentials into this file.

## Related documentation

- [Parent module](../README.md)
- [IDE setup](../../deploy/README.md#preferred-workflow-web-in-the-ide)
- [Host guide](../README.md)
- [Startup](../Configs/Hosting/README.md)
