using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Infrastructure.GitHub;

/// <summary>
///     Normalizes untrusted GitHub log text into bounded terminal-safe lines.
/// </summary>
internal static partial class GitHubWorkflowLogNormalizer
{
    /// <summary>Maximum number of characters retained from one untrusted log line.</summary>
    public const int MaximumLineLength = 16 * 1024;

    /// <summary>Maximum UTF-8 payload published from one job-log download segment.</summary>
    public const int MaximumChunkBytes = 512 * 1024;

    /// <summary>
    ///     Reads normalized lines from a checkpoint, stopping at the per-segment byte limit.
    /// </summary>
    /// <param name="content">Provider response or source text to read within bounds.</param>
    /// <param name="startLine">First normalized log line not yet confirmed as delivered.</param>
    /// <returns>The grouped values described by this operation, preserving unavailable fields.</returns>
    public static (IReadOnlyList<string> Lines, bool HasMore) ReadChunk(string content, int startLine)
    {
        var output = new List<string>();
        var usedBytes = 0;
        foreach (var line in EnumerateLines(content).Skip(startLine))
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (usedBytes + lineBytes > MaximumChunkBytes)
                return (output, true);

            output.Add(line);
            usedBytes += lineBytes;
        }

        return (output, false);
    }

    /// <summary>
    ///     Removes terminal controls and yields bounded CRLF-terminated lines, including blank
    ///     lines.
    /// </summary>
    /// <param name="content">Provider response or source text to read within bounds.</param>
    /// <returns>The enumerate lines selection with its documented bounds and ordering.</returns>
    public static IEnumerable<string> EnumerateLines(string content)
    {
        if (string.IsNullOrEmpty(content)) yield break;

        var position = 0;
        while (position <= content.Length)
        {
            var newline = content.IndexOf('\n', position);
            var end = newline < 0 ? content.Length : newline;
            var rawLine = content[position..end].TrimEnd('\r');
            var line = ControlSequencePattern().Replace(rawLine, string.Empty);
            line = string.Concat(line.Where(character => !char.IsControl(character) || character == '\t'));
            if (line.Length > MaximumLineLength) line = line[..MaximumLineLength] + " [truncated]";
            yield return line + "\r\n";
            if (newline < 0) yield break;
            position = newline + 1;
        }
    }

    /// <summary>
    ///     Computes a stable digest of normalized, redacted log lines.
    /// </summary>
    /// <param name="lines">
    ///     Bounded normalized source-output lines used for stable identity and deduplication.
    /// </param>
    /// <returns>The stable identity derived from the documented metadata or content.</returns>
    public static string Hash(IEnumerable<string> lines)
    {
        return HashWithCount(lines).Hash;
    }

    /// <summary>
    ///     Computes a log digest and line count in one pass.
    /// </summary>
    /// <param name="lines">
    ///     Bounded normalized source-output lines used for stable identity and deduplication.
    /// </param>
    /// <returns>The grouped values described by this operation, preserving unavailable fields.</returns>
    public static (string Hash, int Count) HashWithCount(IEnumerable<string> lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        foreach (var line in lines)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(line));
            count++;
        }

        return (Convert.ToHexString(hash.GetHashAndReset()), count);
    }

    /// <summary>
    ///     Matches ANSI escape sequences that must not reach the terminal or checkpoint.
    /// </summary>
    /// <returns>The cached pattern for this documented normalization or validation rule.</returns>
    [GeneratedRegex("\\x1B(?:[@-_][0-?]*[ -/]*[@-~]|\\][^\\a]*(?:\\a|\\x1B\\\\))", RegexOptions.CultureInvariant)]
    private static partial Regex ControlSequencePattern();
}
