# Git-directory detection tests

| Suite                                                       | Methods/cases | Verifies                                                                             |
|-------------------------------------------------------------|---------------|--------------------------------------------------------------------------------------|
| [GitDirectoryDetectionTests](GitDirectoryDetectionTests.cs) | 2/2           | Case-insensitive .git basenames and rejection of similar names or .git parent paths. |

Candidate strings are supplied directly. No DirectoryInfo metadata, file enumeration, disk existence checks or
repository services are used. These tests verify naming policy rather than repository accessibility.

See the [production guide](../../../Infrastructure/Scanner/Filesystem/README.md) and [parent module](../README.md).
