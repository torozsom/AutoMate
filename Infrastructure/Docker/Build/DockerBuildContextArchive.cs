using System.Formats.Tar;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Docker;

/// <summary>
///     Creates temporary tar archives used as Docker image build contexts.
/// </summary>
/// <param name="options">The current validated options used by this operation.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class DockerBuildContextArchive(DockerOptions options, ILogger logger)
{
    /// <summary>
    ///     Creates a tar build context from a source directory while applying .dockerignore rules.
    /// </summary>
    /// <param name="sourceDirectory">Validated source directory used by project scanning.</param>
    /// <param name="targetTarFilePath">Destination of the Docker build-context archive.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="DirectoryNotFoundException">
    ///     The selected Docker build-context source directory does not exist.
    /// </exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
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
    ///     Deletes the temporary tar archive and logs cleanup failures without hiding the original
    ///     operation result.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    public void DeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Temporary Docker build context cleanup failed: {FailureType}.", ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Temporary Docker build context cleanup failed: {FailureType}.", ex.GetType().Name);
        }
    }

    /// <summary>
    ///     Loads project-specific .dockerignore rules or falls back to configured defaults.
    /// </summary>
    /// <param name="sourceDirectory">Validated source directory used by project scanning.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the ignore.ignore projection derived from the verified inputs.</returns>
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
