using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Scanner;

/// <summary>
///     Loads and caches database provider package matching rules from scanner configuration.
/// </summary>
internal sealed class DatabaseProviderRuleCatalog(ILogger logger)
{
    /// <summary>
    ///     Path to the copied database provider rule JSON file in the application output directory.
    /// </summary>
    private static readonly string DbProvidersJsonPath
        = Path.Combine(AppContext.BaseDirectory, "Scanner", "database-providers.json");

    /// <summary>
    ///     Synchronizes first-load access to the static provider rule cache.
    /// </summary>
    private static readonly SemaphoreSlim DbProvidersSemaphore = new(1, 1);

    /// <summary>
    ///     Process-wide cache of database provider rules.
    /// </summary>
    private static List<DbProviderRuleDto>? _cachedDbProviders;

    /// <summary>
    ///     Gets cached database provider rules or loads them from disk on first use.
    /// </summary>
    public async Task<IReadOnlyList<DbProviderRuleDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (_cachedDbProviders != null)
            return _cachedDbProviders;

        if (!File.Exists(DbProvidersJsonPath))
        {
            logger.LogWarning(
                "[ProjectScannerService] Database providers JSON file not found at: {DbProvidersJsonPath}",
                DbProvidersJsonPath);
            return [];
        }

        await DbProvidersSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedDbProviders != null)
                return _cachedDbProviders;

            await using var stream = new FileStream(DbProvidersJsonPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

            _cachedDbProviders =
                await JsonSerializer.DeserializeAsync<List<DbProviderRuleDto>>(stream,
                    cancellationToken: cancellationToken) ?? [];

            return _cachedDbProviders;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "[ProjectScannerService] Operation canceled while parsing database-providers.json");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ProjectScannerService] Failed to parse database-providers.json");
            return [];
        }
        finally
        {
            DbProvidersSemaphore.Release();
        }
    }
}