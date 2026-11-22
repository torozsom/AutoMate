using System.Text.Json;
using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies indexed configuration paths for scalar and object arrays.
/// </summary>
public sealed class JsonArrayFlatteningTests
{
    /// <summary>
    ///     Arrays retain zero-based indexes in scalar and object paths beneath an existing prefix.
    /// </summary>
    [Fact]
    public void Flatten_ArraysWithPrefix_ReturnsIndexedPathsAndPreservedValues()
    {
        using var document = JsonDocument.Parse("""
                                                {"Hosts":["api","worker"],"Services":[{"Port":8080},{"Port":9090}]}
                                                """);
        var actual = new Dictionary<string, string>();

        JsonConfigurationFlattener.Flatten(document.RootElement, "App", actual);

        Assert.Equal(4, actual.Count);
        Assert.Equal("api", actual["App__Hosts__0"]);
        Assert.Equal("worker", actual["App__Hosts__1"]);
        Assert.Equal("8080", actual["App__Services__0__Port"]);
        Assert.Equal("9090", actual["App__Services__1__Port"]);
    }
}
