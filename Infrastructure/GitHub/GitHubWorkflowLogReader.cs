using System.IO.Compression;
using System.Text;
using Domain.DTO;

namespace Infrastructure.GitHub;

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
        var builder = new StringBuilder();

        foreach (var entry in await ReadEntriesAsync(zipStream, cancellationToken))
        {
            builder.AppendLine();
            builder.AppendLine($"===== {entry.Path} =====");
            builder.AppendLine(entry.Content);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>Reads stable archive entry names alongside their log content for selective reconciliation.</summary>
    public static async Task<IReadOnlyList<GitHubWorkflowLogArchiveEntryDto>> ReadEntriesAsync(Stream zipStream,
        CancellationToken cancellationToken)
    {
        await using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entries = new List<GitHubWorkflowLogArchiveEntryDto>();
        foreach (var entry in archive.Entries.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                     .OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var entryStream = await entry.OpenAsync(cancellationToken);
            using var reader = new StreamReader(entryStream, Encoding.UTF8, true);
            entries.Add(new GitHubWorkflowLogArchiveEntryDto(entry.FullName,
                await reader.ReadToEndAsync(cancellationToken)));
        }

        return entries;
    }
}
