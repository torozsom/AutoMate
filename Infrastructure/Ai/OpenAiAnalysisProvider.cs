using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>Direct OpenAI adapter retaining its exact regional route and independent credential setting.</summary>
public sealed class OpenAiAnalysisProvider(
    HttpClient client,
    IConfiguration configuration,
    IOptionsMonitor<AiAnalysisOptions> options,
    IAnalysisResultValidator validator,
    IDiagnosticRedactor redactor,
    IAnalysisEgressAuthorizer egress) : ILlmAnalysisProvider
{
    /// <summary>Version of the shared fixed untrusted-diagnostics instructions.</summary>
    internal const string PromptVersion = ResponsesAnalysisTransport.PromptVersion;

    /// <summary>Maximum UTF-8 response body retained before parsing.</summary>
    internal const int MaximumResponseBytes = ResponsesAnalysisTransport.MaximumResponseBytes;

    /// <summary>Shared protocol implementation; this adapter owns the approved direct OpenAI route.</summary>
    private readonly ResponsesAnalysisTransport _transport = new(client, options, validator, redactor, egress);

    /// <inheritdoc />
    public Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        return _transport.AnalyzeAsync(request, "openai", () => configuration["AiAnalysis:ApiKey"],
            AnalysisEgressPolicy.IsConfigured, cancellationToken);
    }
}