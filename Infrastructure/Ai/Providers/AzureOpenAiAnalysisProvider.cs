using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>Azure OpenAI Responses v1 adapter using an explicit resource API key and deployment alias.</summary>
public sealed class AzureOpenAiAnalysisProvider(
    HttpClient client,
    IConfiguration configuration,
    IOptionsMonitor<AiAnalysisOptions> options,
    IAnalysisResultValidator validator,
    IDiagnosticRedactor redactor,
    IAnalysisEgressAuthorizer egress) : ILlmAnalysisProvider
{
    /// <summary>Canonical catalog and persisted provenance identifier.</summary>
    public const string ProviderName = "azure-openai";

    /// <summary>Shared bounded protocol; Azure credentials and routing remain adapter-specific.</summary>
    private readonly ResponsesAnalysisTransport _transport = new(client, options, validator, redactor, egress);

    /// <inheritdoc />
    public Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        return _transport.AnalyzeAsync(request, ProviderName, () => ReadOptions(configuration).ApiKey,
            settings => ReadOptions(configuration).ApprovesRoute(settings), cancellationToken);
    }

    /// <summary>Reads a fresh typed Azure snapshot; never falls back to a direct OpenAI key or another principal.</summary>
    public static AzureOpenAiOptions ReadOptions(IConfiguration configuration)
    {
        return configuration.GetSection(AzureOpenAiOptions.SectionName).Get<AzureOpenAiOptions>() ??
               new AzureOpenAiOptions();
    }
}