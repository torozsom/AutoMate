using System.Xml.Linq;
using Domain.DTO.Projects;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Scanner.Projects;

/// <summary>
///     Parses local C# project files discovered during repository scans.
/// </summary>
internal static class LocalCsProjectParser
{
    /// <summary>SDK marker used to classify ASP.NET Core web projects.</summary>
    private const string WebSdkName = "Microsoft.NET.Sdk.Web";

    /// <summary>
    ///     Creates a C# project DTO from a .csproj file path and web SDK detection.
    /// </summary>
    /// <param name="csprojPath">Local C# project-file path.</param>
    /// <param name="logger">
    ///     Receives reviewed operational events without diagnostic payloads or credentials.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the cs project dto projection derived from the verified inputs.</returns>
    public static async Task<CsProjectDto> ParseAsync(string csprojPath, ILogger logger,
        CancellationToken cancellationToken)
    {
        var isWeb = false;
        try
        {
            await using var stream =
                new FileStream(csprojPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

            var sdkAttribute = document.Root?.Attribute("Sdk")?.Value;
            isWeb = sdkAttribute != null &&
                    sdkAttribute.Contains(WebSdkName, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(
                "[LocalSystemScannerService] Parsing cancelled for .csproj file: {CsProjPath}, Exception: {Exception}",
                csprojPath, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[LocalSystemScannerService] Failed to parse .csproj file to determine project type: {CsProjPath}",
                csprojPath);
        }

        return new CsProjectDto
        {
            Name = Path.GetFileNameWithoutExtension(csprojPath),
            Path = csprojPath,
            IsWebProject = isWeb
        };
    }
}
