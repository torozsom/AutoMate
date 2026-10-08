using Application.Ai;

namespace Infrastructure.Ai;

/// <summary>Azure resource identity and protected API-key configuration; never used as result or telemetry data.</summary>
public sealed class AzureOpenAiOptions
{
    /// <summary>Nested section reloads also invalidate the enclosing AI policy snapshot and cancel active transport.</summary>
    public const string SectionName = "AiAnalysis:AzureOpenAi";

    /// <summary>Exact approved Azure OpenAI resource DNS label, without suffix or URL.</summary>
    public string? ResourceName { get; init; }

    /// <summary>Resource key supplied through Web user-secrets or protected deployment configuration.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Approves only the configured public Azure resource's canonical HTTPS Responses v1 base URL.</summary>
    public bool ApprovesRoute(AiAnalysisOptions settings)
    {
        return settings.Provider == AzureOpenAiAnalysisProvider.ProviderName &&
               ResourceName is { Length: >= 2 and <= 63 } &&
               ResourceName[0] != '-' && ResourceName[^1] != '-' &&
               ResourceName.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
               settings.Endpoint == $"https://{ResourceName}.openai.azure.com/openai/v1/";
    }

    /// <summary>Checks only local header-safe credential presence; performs no inference or credential I/O.</summary>
    public bool HasApiKey()
    {
        return ResponsesAnalysisTransport.IsValidApiKey(ApiKey);
    }
}