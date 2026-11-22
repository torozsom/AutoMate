using System.Text.Json;
using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies nested JSON object paths and preserved leaf values.
/// </summary>
public sealed class JsonObjectFlatteningTests
{
    /// <summary>
    ///     Nested object paths use double underscores while preserving leaf names and values.
    /// </summary>
    [Fact]
    public void Flatten_NestedObjects_ReturnsDoubleUnderscorePaths()
    {
        using var document = JsonDocument.Parse("""
                                                {"App":{"Name":"Demo","Logging":{"Level":"Warning"}},"Port":8080}
                                                """);
        var actual = new Dictionary<string, string>();

        JsonConfigurationFlattener.Flatten(document.RootElement, string.Empty, actual);

        Assert.Equal(3, actual.Count);
        Assert.Equal("Demo", actual["App__Name"]);
        Assert.Equal("Warning", actual["App__Logging__Level"]);
        Assert.Equal("8080", actual["Port"]);
    }
}
