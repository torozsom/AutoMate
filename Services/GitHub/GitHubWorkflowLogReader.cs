using System.IO.Compression;
using System.Text;

namespace Services.GitHub;

/// <summary>
///     Flattens GitHub Actions workflow log archives into terminal-friendly text.
/// </summary>
internal static class GitHubWorkflowLogReader
{
    /// <summary>
    ///     Reads all file entries from a GitHub workflow log ZIP stream in stable filename order.
    /// </summary>
    public static async Task<string?> ReadFlattenedLogsAsync(Stream zipStream, CancellationToken cancellationToken)
    {
        await using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var builder = new StringBuilder();

        foreach (var entry in archive.Entries
                     .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                     .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            builder.AppendLine();
            builder.AppendLine($"===== {entry.FullName} =====");

            await using var entryStream = await entry.OpenAsync(cancellationToken);
            using var reader = new StreamReader(entryStream, Encoding.UTF8, true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            builder.AppendLine(content);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}