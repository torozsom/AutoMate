# Environment parsing tests

| Suite                                                     | Methods/cases | Verifies                                                                              |
|-----------------------------------------------------------|---------------|---------------------------------------------------------------------------------------|
| [DotEnvAssignmentTests](DotEnvAssignmentTests.cs)         | 2/5           | Whitespace and explicitly empty values.                                               |
| [DotEnvExportTests](DotEnvExportTests.cs)                 | 1/2           | Optional export prefixes.                                                             |
| [DotEnvQuotationTests](DotEnvQuotationTests.cs)           | 1/2           | Matching outer quotes and preserved inner whitespace.                                 |
| [DotEnvCommentTests](DotEnvCommentTests.cs)               | 2/5           | Blank lines, full-line comments and supported unquoted inline comments.               |
| [DotEnvMalformedInputTests](DotEnvMalformedInputTests.cs) | 1/3           | Missing separators or keys.                                                           |
| [DotEnvValueTests](DotEnvValueTests.cs)                   | 1/1           | Embedded equals signs.                                                                |
| [JsonObjectFlatteningTests](JsonObjectFlatteningTests.cs) | 1/1           | Nested object paths and leaf values.                                                  |
| [JsonArrayFlatteningTests](JsonArrayFlatteningTests.cs)   | 1/1           | Array indexes and supplied prefixes.                                                  |
| [JsonScalarFlatteningTests](JsonScalarFlatteningTests.cs) | 3/3           | Boolean/null conversion, empty containers, accumulator preservation and root scalars. |

Tests document supported scanner syntax, not full shell or dotenv compliance. Quoted inline-comment semantics
are outside these suites. JSON documents are in memory and disposed within each test.
Exact key/value and entry-count assertions detect missing or additional flattened entries.

See the [production guide](../../../Infrastructure/Scanner/Environment/README.md) and [parent module](../README.md).
