using System.Text;
using Infrastructure.Docker.Diagnostics;
using Xunit;

namespace Tests.Docker.Diagnostics;

/// <summary>
///     Verifies UTF-8 decoding when character boundaries differ from frame boundaries.
/// </summary>
public sealed class DockerLogUtf8Tests
{
    /// <summary>
    ///     An empty frame does not manufacture an empty log line.
    /// </summary>
    [Fact]
    public void Feed_EmptyFrame_ReturnsNoLines()
    {
        var decoder = new DockerLogDecoder(false, TimeProvider.System);

        var actual = decoder.Feed([]);

        Assert.Empty(actual);
    }

    /// <summary>
    ///     Every byte split retains Unicode, CRLF and a final partial line with or without provider timestamps.
    /// </summary>
    /// <param name="timestamps">Whether each source line includes a provider timestamp prefix.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Feed_EveryUtf8FrameBoundary_PreservesLinesAndTimestampMode(bool timestamps)
    {
        const string stamp = "2026-10-04T08:00:00.123456789Z";
        var prefix = timestamps ? stamp + " " : string.Empty;
        var bytes = Encoding.UTF8.GetBytes($"{prefix} árvíz café 😀\r\n{prefix}\n{prefix}partial");

        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new DockerLogDecoder(timestamps, TimeProvider.System);
            var lines = decoder.Feed(bytes.AsSpan(0, split)).Concat(decoder.Feed(bytes.AsSpan(split), true)).ToArray();

            Assert.Equal([" árvíz café 😀\r\n", "\n", "partial"], lines.Select(e => e.Text));
            Assert.Equal(Enumerable.Repeat(timestamps ? stamp : null, lines.Length),
                lines.Select(e => e.ProviderTimestamp));
            Assert.DoesNotContain(lines, e => e.Omitted);
        }
    }
}
