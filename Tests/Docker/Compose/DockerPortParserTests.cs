using Infrastructure.Docker.Compose;
using Xunit;

namespace Tests.Docker.Compose;

/// <summary>
///     Verifies host-port extraction from synthetic Docker CLI output.
/// </summary>
public sealed class DockerPortParserTests
{
    /// <summary>
    ///     A recognized mapping returns the host port rather than the container port.
    /// </summary>
    /// <param name="output">A valid Docker port mapping.</param>
    /// <param name="expected">The mapped host port.</param>
    [Theory]
    [InlineData("8080/tcp -> 0.0.0.0:49153", 49153)]
    [InlineData("443/tcp -> 127.0.0.1:8443", 8443)]
    [InlineData("80/tcp -> [::]:8080", 8080)]
    public void ParseHostPort_ValidMapping_ReturnsHostPort(string output, int expected)
    {
        var actual = DockerPortParser.ParseHostPort(output);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     Multiple mappings retain the first recognized host port in command-output order.
    /// </summary>
    [Fact]
    public void ParseHostPort_MultipleMappings_ReturnsFirstHostPort()
    {
        const string output = "80/tcp -> 0.0.0.0:8080\n443/tcp -> 0.0.0.0:8443";

        var actual = DockerPortParser.ParseHostPort(output);

        Assert.Equal(8080, actual);
    }

    /// <summary>
    ///     Output without a recognizable mapping returns the existing unavailable-port sentinel.
    /// </summary>
    /// <param name="output">Synthetic Docker command output.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("8080/tcp")]
    [InlineData("80/tcp -> localhost:unknown")]
    public void ParseHostPort_NoRecognizableMapping_ReturnsZero(string output)
    {
        var actual = DockerPortParser.ParseHostPort(output);

        Assert.Equal(0, actual);
    }
}
