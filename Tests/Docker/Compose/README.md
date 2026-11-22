# Compose parsing and naming tests

| Suite                                                                 | Methods | Verifies                                                                        |
|-----------------------------------------------------------------------|---------|---------------------------------------------------------------------------------|
| [DockerNameNormalizerTests](DockerNameNormalizerTests.cs)             | 4       | Casing/whitespace, invalid separators, supported characters and fallback names. |
| [DockerComposeProjectParserTests](DockerComposeProjectParserTests.cs) | 3       | Source order, empty output and entries without names.                           |
| [DockerPortParserTests](DockerPortParserTests.cs)                     | 3       | Host rather than container ports, first mapping and unavailable-port sentinel.  |

Inputs are strings; output assertions check exact names, ordered collections and numeric ports.
Regex helpers remain within the Docker module. These tests do not establish daemon availability or deployment health.

See the [production Compose guide](../../../Infrastructure/Docker/Compose/README.md) and [parent module](../README.md).
