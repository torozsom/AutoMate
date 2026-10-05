namespace Application.Ai;

/// <summary>Fail-closed policy for the initially supported provider, processing geographies and context categories.</summary>
public static class AnalysisEgressPolicy
{
    /// <summary>
    ///     Requires every current context category and an exact HTTPS regional base URL; never approves global/proxy
    ///     endpoints.
    /// </summary>
    public static bool IsConfigured(AiAnalysisOptions options)
    {
        return IsCommonConfigured(options) && options.Provider == "openai" &&
               options.Endpoint == $"https://{options.ProcessingRegion}.api.openai.com/v1/";
    }

    /// <summary>Provider-independent approval/budget requirements; adapters must additionally approve their exact route.</summary>
    public static bool IsCommonConfigured(AiAnalysisOptions options)
    {
        if (!options.Enabled || !options.ProviderEgressEnabled ||
            !options.RegionalProcessingApproved || options.ProcessingRegion is not ("us" or "eu") ||
            options.ApprovedRegions?.Contains(options.ProcessingRegion, StringComparer.Ordinal) != true ||
            options.ApprovedTenantIds is not { Length: > 0 } || options.ApprovedTenantIds.Contains(Guid.Empty) ||
            options.AllowedDataCategories is not { Length: 3 } ||
            options.Model is not { Length: >= 1 and <= 128 } ||
            !options.Model.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') ||
            options.ResultRetentionDays is < 1 or > 90 ||
            options.TimeoutSeconds is < 5 or > 300 ||
            options.MaximumOutputTokens is < 1 or > 8192 ||
            options.MaximumContextCharacters is < 1 or > 131_072 ||
            options.MaximumContextBytes is < 1 or > 131_072 ||
            options.MaximumContextTokens is < 1 or > 131_072) return false;
        var categories = options.AllowedDataCategories.ToHashSet(StringComparer.Ordinal);
        if (!categories.SetEquals(["logs", "metrics", "traceCorrelation"])) return false;
        return true;
    }
}