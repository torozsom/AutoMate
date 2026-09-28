using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Infrastructure.GitHub;

/// <summary>Normalizes untrusted GitHub log text into bounded terminal-safe lines.</summary>
internal static partial class GitHubWorkflowLogNormalizer
{
    public const int MaximumLineLength = 16 * 1024;
    public const int MaximumPollBytes = 512 * 1024;

    public static IReadOnlyList<string> Normalize(string content)
    {
        if (string.IsNullOrEmpty(content)) return [];

        var output = new List<string>();
        var usedBytes = 0;
        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = ControlSequencePattern().Replace(rawLine, string.Empty);
            line = string.Concat(line.Where(character => !char.IsControl(character) || character == '\t'));
            if (line.Length > MaximumLineLength) line = line[..MaximumLineLength] + " [truncated]";

            var lineWithNewline = line + "\r\n";
            var lineBytes = Encoding.UTF8.GetByteCount(lineWithNewline);
            if (usedBytes + lineBytes > MaximumPollBytes)
            {
                output.Add("[GitHub log output truncated for this poll.]\r\n");
                break;
            }

            output.Add(lineWithNewline);
            usedBytes += lineBytes;
        }

        return output;
    }

    public static string Hash(IEnumerable<string> lines)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines))));
    }

    [GeneratedRegex("\\x1B(?:[@-_][0-?]*[ -/]*[@-~]|\\][^\\a]*(?:\\a|\\x1B\\\\))", RegexOptions.CultureInvariant)]
    private static partial Regex ControlSequencePattern();
}
