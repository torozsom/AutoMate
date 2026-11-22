# Docker CLI construction tests

| Suite                                                         | Methods/cases | Verifies                                                                   |
|---------------------------------------------------------------|---------------|----------------------------------------------------------------------------|
| [DockerProcessOptionsTests](DockerProcessOptionsTests.cs)     | 2/2           | Executable, stdout/stderr redirection, shell exclusion and hidden windows. |
| [DockerComposeArgumentsTests](DockerComposeArgumentsTests.cs) | 2/2           | Working directory, argument order and filenames containing spaces.         |

Tests inspect ProcessStartInfo values only; they never launch a process or check a working directory on disk.

See the [production guide](../../../Infrastructure/Docker/Cli/README.md) and [parent module](../README.md).
