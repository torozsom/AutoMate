namespace Services.Scanner;

/// <summary>
///     Parses individual .env file lines into environment variable pairs.
/// </summary>
internal static class DotEnvLineParser
{
    /// <summary>
    ///     Optional export prefix accepted in .env files.
    /// </summary>
    private const string ExportPrefix = "export ";

    /// <summary>
    ///     Parses a non-empty, non-comment .env line into a key-value pair.
    /// </summary>
    public static bool TryParse(string line, out KeyValuePair<string, string> variable)
    {
        variable = default;

        var trimmedLine = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
            return false;

        if (trimmedLine.StartsWith(ExportPrefix, StringComparison.OrdinalIgnoreCase))
            trimmedLine = trimmedLine[ExportPrefix.Length..].TrimStart();

        var commentIndex = trimmedLine.IndexOf(" #", StringComparison.Ordinal);
        if (commentIndex > 0)
            trimmedLine = trimmedLine[..commentIndex].Trim();

        var splitIndex = trimmedLine.IndexOf('=');
        if (splitIndex <= 0)
            return false;

        var key = trimmedLine[..splitIndex].Trim();
        var value = trimmedLine[(splitIndex + 1)..].Trim();

        if (value.Length > 1 &&
            ((value.StartsWith('"') && value.EndsWith('"')) ||
             (value.StartsWith('\'') && value.EndsWith('\''))))
            value = value[1..^1];

        variable = new KeyValuePair<string, string>(key, value);
        return true;
    }
}