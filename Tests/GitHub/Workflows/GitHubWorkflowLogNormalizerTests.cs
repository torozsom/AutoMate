using Xunit;
using GitHubWorkflowLogNormalizer = Infrastructure.GitHub.Workflows.GitHubWorkflowLogNormalizer;

namespace Tests.GitHub.Workflows;

/// <summary>
///     Verifies safe normalization and bounded paging of GitHub Actions log text.
/// </summary>
public sealed class GitHubWorkflowLogNormalizerTests
{
    /// <summary>
    ///     Ensures escape sequences are removed and line endings are terminal-safe.
    /// </summary>
    [Fact]
    public void EnumerateLines_removes_terminal_escape_sequences_and_normalizes_newlines()
    {
        var result = GitHubWorkflowLogNormalizer.EnumerateLines("first\r\n\u001b[31msecond\u001b[0m\nthird");

        Assert.Equal(["first\r\n", "second\r\n", "third\r\n"], result);
    }

    /// <summary>
    ///     Ensures one untrusted line cannot exceed the configured display limit.
    /// </summary>
    [Fact]
    public void EnumerateLines_truncates_an_oversized_line()
    {
        var result =
            GitHubWorkflowLogNormalizer.EnumerateLines(new string('x',
                GitHubWorkflowLogNormalizer.MaximumLineLength + 1));

        var line = Assert.Single(result);

        Assert.EndsWith(" [truncated]\r\n", line);
        Assert.Equal(GitHubWorkflowLogNormalizer.MaximumLineLength + " [truncated]\r\n".Length, line.Length);
    }

    /// <summary>
    ///     Ensures a subsequent segment resumes without losing output.
    /// </summary>
    [Fact]
    public void ReadChunk_resumes_after_the_bounded_first_chunk_without_dropping_lines()
    {
        var content = string.Join('\n', Enumerable.Range(0, 40).Select(index =>
            $"{index:D2}:{new string('x', GitHubWorkflowLogNormalizer.MaximumLineLength - 10)}"));

        var first = GitHubWorkflowLogNormalizer.ReadChunk(content, 0);
        var second = GitHubWorkflowLogNormalizer.ReadChunk(content, first.Lines.Count);

        Assert.True(first.HasMore);
        Assert.NotEmpty(second.Lines);
        Assert.Equal(GitHubWorkflowLogNormalizer.EnumerateLines(content), first.Lines.Concat(second.Lines));
        Assert.False(second.HasMore);
    }
}
