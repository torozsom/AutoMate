# Environment extraction

Configuration flattening and ordered .env/launch-settings extraction for deployment forms.

## Contents

| File                                                                             | Types or asset                                                    | Responsibility                                                               |
|----------------------------------------------------------------------------------|-------------------------------------------------------------------|------------------------------------------------------------------------------|
| [DotEnvLineParser.cs](DotEnvLineParser.cs)                                       | `DotEnvLineParser`                                                | Parses individual .env file lines into environment variable pairs.           |
| [JsonConfigurationFlattener.cs](JsonConfigurationFlattener.cs)                   | `JsonConfigurationFlattener`                                      | Flattens JSON configuration into double-underscore separated keys.           |
| [ProjectEnvironmentVariableExtractor.cs](ProjectEnvironmentVariableExtractor.cs) | `ProjectEnvironmentVariableExtractor`, `LaunchSettingsScanResult` | Extracts environment variables from common .NET project configuration files. |

## Usage and integration

ProjectEnvironmentVariableExtractor merges appsettings, .env and Project-profile launch settings into a case-insensitive
dictionary. JsonConfigurationFlattener creates double-underscore paths; DotEnvLineParser handles export prefixes,
key/value pairs, matching outer quotes and supported comment syntax.

## Contracts and lifecycle

Appsettings files are processed in ordinal-ignore-case filename order; .env values override appsettings and launch
profile variables override both. Parent .env is read before project .env. ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT
are removed because the form owns the environment choice.

This is the existing extraction convention, not a full .NET configuration-provider or shell parser. Do not imply
interpolation or shell evaluation. Malformed optional JSON is skipped, while filesystem/access/cancellation failures
propagate to safe form guidance. A successful empty map differs from failed extraction. Values may contain credentials
and must not be logged.

## Related documentation

- [Parent module](../README.md)
- [Extraction consumer](../Projects/README.md)
- [Port](../../../Application/Abstractions/Scanning/README.md)
- [Loading tests](../../../Test/Web/Projects/ConfigurationVariableLoadingTests.cs)
- [Environment destination](../../../Domain/DTO/Deployment/README.md)
