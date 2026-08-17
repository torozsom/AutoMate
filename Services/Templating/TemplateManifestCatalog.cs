using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Templating;

/// <summary>
///     Loads and caches template manifest rules used by the templating service.
/// </summary>
internal sealed class TemplateManifestCatalog(ILogger logger)
{
    /// <summary>
    ///     Synchronizes first-load access to the static manifest cache.
    /// </summary>
    private static readonly SemaphoreSlim ManifestSemaphore = new(1, 1);

    /// <summary>
    ///     Process-wide cache of parsed template manifest rules.
    /// </summary>
    private static List<TemplateManifestRuleDto>? _cachedManifest;

    /// <summary>
    ///     Gets template manifest rules from cache or loads them from disk.
    /// </summary>
    public async Task<IReadOnlyList<TemplateManifestRuleDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (_cachedManifest != null)
            return _cachedManifest;

        if (!File.Exists(TemplatePaths.ManifestPath))
        {
            logger.LogError("[TemplateService] Template manifest not found at: {ManifestPath}",
                TemplatePaths.ManifestPath);
            throw new FileNotFoundException($"Template manifest not found: {TemplatePaths.ManifestPath}");
        }

        await ManifestSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedManifest != null)
                return _cachedManifest;

            await using var stream =
                new FileStream(TemplatePaths.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    true);
            _cachedManifest =
                await JsonSerializer.DeserializeAsync<List<TemplateManifestRuleDto>>(stream,
                    cancellationToken: cancellationToken) ?? [];

            return _cachedManifest;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("[TemplateService] Template manifest loading cancelled. Exception: {Exception}",
                ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TemplateService] Failed to parse template-manifest.json");
            return [];
        }
        finally
        {
            ManifestSemaphore.Release();
        }
    }
}