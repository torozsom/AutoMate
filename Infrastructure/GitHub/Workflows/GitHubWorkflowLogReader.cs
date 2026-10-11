using System.IO.Compression;
using System.Text;
using Domain.DTO;

namespace Infrastructure.GitHub;

/// <summary>Reads GitHub Actions job logs and named workflow archive entries.</summary>
internal static class GitHubWorkflowLogReader
{
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

    /// <summary>Reads a job ZIP in entry order, accepting plain-text responses as a fallback.</summary>
    public static async Task<string?> ReadTextAsync(Stream zipStream, CancellationToken cancellationToken)
    {
        var buffer = EnsureSeekableCopy(zipStream);
        buffer.Position = 0;

        try
        {
            var entries = await ReadEntriesAsync(buffer, cancellationToken);
            if (entries.Count == 0) return null;

            var builder = new StringBuilder();
            foreach (var entry in entries)
            {
                if (builder.Length > 0 && !builder[^1].Equals('\n')) builder.AppendLine();
                builder.Append(entry.Content);
                if (!string.IsNullOrEmpty(entry.Content) &&
                    !entry.Content.EndsWith("\n", StringComparison.Ordinal) &&
                    !entry.Content.EndsWith("\r\n", StringComparison.Ordinal))
                    builder.AppendLine();
            }

            return builder.ToString();
        }
        catch (InvalidDataException)
        {
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            return string.IsNullOrEmpty(content) ? null : content;
        }
    }

    /// <summary>Copies the response to a seekable buffer for ZIP detection and text fallback.</summary>
    private static MemoryStream EnsureSeekableCopy(Stream source)
    {
        if (source.CanSeek)
            source.Position = 0;

        var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        return buffer;
    }
}