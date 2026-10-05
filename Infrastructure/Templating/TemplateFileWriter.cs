using Domain.DTO;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Templating;

/// <summary>
///     Writes generated template files to disk with output path safety checks.
/// </summary>
internal sealed class TemplateFileWriter(ILogger logger)
{
    /// <summary>
    ///     Saves every generated template file beneath the requested output directory.
    /// </summary>
    public async Task WriteAllAsync(string outputDirectory, IEnumerable<TemplateFile> generatedFiles,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        foreach (var file in generatedFiles)
        {
            var outputPath = TemplatePaths.ResolveOutputPath(outputDirectory, file.Path);
            var outputDir = Path.GetDirectoryName(outputPath);

            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            await File.WriteAllTextAsync(outputPath, file.Content, cancellationToken);
            logger.LogInformation("Deployment artifact saved: {OutputFile}.", Path.GetFileName(file.Path));
        }
    }
}
