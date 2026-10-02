using FluentAssertions;
using Infrastructure.GitHub;

namespace Infrastructure.Tests.GitHub;

public sealed class GitHubWorkflowLogNormalizerTests
{
    [Fact]
    public void Normalize_removes_terminal_escape_sequences_and_normalizes_newlines()
    {
        var result = GitHubWorkflowLogNormalizer.Normalize("first\r\n\u001b[31msecond\u001b[0m\nthird");

        result.Should().Equal("first\r\n", "second\r\n", "third\r\n");
    }

    [Fact]
    public void Normalize_truncates_an_oversized_line()
    {
        var result =
            GitHubWorkflowLogNormalizer.Normalize(new string('x', GitHubWorkflowLogNormalizer.MaximumLineLength + 1));

        result.Should().ContainSingle().Which.Should().EndWith(" [truncated]\r\n")
            .And.HaveLength(GitHubWorkflowLogNormalizer.MaximumLineLength + " [truncated]\r\n".Length);
    }
}