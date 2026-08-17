using System.Formats.Tar;
using Microsoft.Extensions.Logging;

namespace Services.Docker;

/// <summary>
///     Creates temporary tar archives used as Docker image build contexts.
/// </summary>
internal sealed class DockerBuildContextArchive(DockerOptions options, ILogger logger)
{
    /// <summary>
    ///     Creates a tar build context from a source directory while applying .dockerignore rules.
    /// </summary>
    public async Task CreateAsync(string sourceDirectory, string targetTarFilePath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory '{sourceDirectory}' does not exist.");

        var ignore = await CreateIgnoreRulesAsync(sourceDirectory, cancellationToken);

        await using var fileStream = new FileStream(targetTarFilePath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var tarWriter = new TarWriter(fileStream);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            if (!ignore.IsIgnored(relativePath))
                await tarWriter.WriteEntryAsync(filePath, relativePath, cancellationToken);
        }
    }

    /// <summary>
    ///     Deletes the temporary tar archive and logs cleanup failures without hiding the original operation result.
    /// </summary>
    public void DeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "[DockerService] Failed to delete temporary Docker build context '{Path}'.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "[DockerService] Failed to delete temporary Docker build context '{Path}'.", path);
        }
    }

    /// <summary>
    ///     Loads project-specific .dockerignore rules or falls back to configured defaults.
    /// </summary>
    private async Task<Ignore.Ignore> CreateIgnoreRulesAsync(string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var ignore = new Ignore.Ignore();
        var dockerIgnorePath = Path.Combine(sourceDirectory, ".dockerignore");

        if (File.Exists(dockerIgnorePath))
        {
            var lines = await File.ReadAllLinesAsync(dockerIgnorePath, cancellationToken);
            ignore.Add(lines);
        }
        else
        {
            ignore.Add(options.DefaultDockerIgnore);
        }

        return ignore;
    }
}