using System.Text.Json;
using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies scalar configuration conversion and handling of empty containers.
/// </summary>
public sealed class JsonScalarFlatteningTests
{
    /// <summary>
    ///     Boolean and null leaves produce the existing string representations without dropping keys.
    /// </summary>
    [Fact]
    public void Flatten_BooleanAndNullValues_PreservesScalarRepresentations()
    {
        using var document = JsonDocument.Parse("""{"Enabled":true,"Disabled":false,"Optional":null}""");
        var actual = new Dictionary<string, string>();

        JsonConfigurationFlattener.Flatten(document.RootElement, string.Empty, actual);

        Assert.Equal(3, actual.Count);
        Assert.Equal("True", actual["Enabled"]);
        Assert.Equal("False", actual["Disabled"]);
        Assert.Equal(string.Empty, actual["Optional"]);
    }

    /// <summary>
    ///     Empty objects and arrays do not create phantom leaves or erase the caller-provided accumulator.
    /// </summary>
    [Fact]
    public void Flatten_EmptyContainers_AddsNoEntriesAndPreservesExistingValues()
    {
        using var document = JsonDocument.Parse("""{"Object":{},"Array":[]}""");
        var actual = new Dictionary<string, string> { ["Existing"] = "retained" };

        JsonConfigurationFlattener.Flatten(document.RootElement, string.Empty, actual);

        Assert.Equal("retained", Assert.Single(actual).Value);
        Assert.True(actual.ContainsKey("Existing"));
    }

    /// <summary>
    ///     Flattening a root scalar assigns its value to the supplied configuration prefix.
    /// </summary>
    [Fact]
    public void Flatten_RootScalar_UsesProvidedPrefix()
    {
        using var document = JsonDocument.Parse("42");
        var actual = new Dictionary<string, string>();

        JsonConfigurationFlattener.Flatten(document.RootElement, "Retries", actual);

        Assert.Equal("42", Assert.Single(actual).Value);
        Assert.True(actual.ContainsKey("Retries"));
    }
}
