using Application.Abstractions.Ai.Policy;
using Application.Abstractions.Ai.Results;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Ai.Policy;
using Application.Ai.Results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai.Providers;

/// <summary>
///     Azure OpenAI Responses v1 adapter using an explicit resource API key and deployment
///     alias.
/// </summary>
/// <param name="client">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="configuration">
///     Configuration sources used to read current provider and hosting settings.
/// </param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="validator">Rejects unsupported output and returns independent safe result copies.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="egress">Rechecks current ownership, consent and operator egress approval.</param>
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

    /// <summary>
    ///     Obtains bounded structured output without granting model tools or deployment
    ///     permissions.
    /// </summary>
    /// <param name="request">The redacted evidence request and deployment egress scope.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning untrusted structured analysis output that must be validated and
    ///     redacted before use.
    /// </returns>
    /// <exception cref="T:AnalysisProviderUnavailableException">
    ///     Current provider configuration, operator policy or deployment consent prevents
    ///     execution.
    /// </exception>
    /// <exception cref="T:AnalysisProviderAccessDeniedException">
    ///     The provider rejects access to the configured resource or model.
    /// </exception>
    /// <exception cref="T:TransientAnalysisProviderException">
    ///     The provider or transport reports a failure eligible for bounded retry handling.
    /// </exception>
    /// <exception cref="T:InvalidAnalysisResultException">
    ///     The provider output violates the supported response, provenance or evidence contract.
    /// </exception>
    /// <exception cref="T:System.OperationCanceledException">
    ///     The caller cancels the provider request.
    /// </exception>
    /// <exception cref="T:System.ArgumentException">
    ///     The supplied context exceeds configured input limits.
    /// </exception>
    /// <exception cref="T:System.Net.Http.HttpRequestException">
    ///     The provider reports a permanent HTTP failure.
    /// </exception>
    public Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        return _transport.AnalyzeAsync(request, ProviderName, () => ReadOptions(configuration).ApiKey,
            settings => ReadOptions(configuration).ApprovesRoute(settings), cancellationToken);
    }

    /// <summary>
    ///     Reads a fresh typed Azure snapshot; never falls back to a direct OpenAI key or another
    ///     principal.
    /// </summary>
    /// <param name="configuration">
    ///     Configuration sources used to read current provider and hosting settings.
    /// </param>
    /// <returns>A fresh Azure provider-options snapshot, using defaults when the section is absent.</returns>
    public static AzureOpenAiOptions ReadOptions(IConfiguration configuration)
    {
        return configuration.GetSection(AzureOpenAiOptions.SectionName).Get<AzureOpenAiOptions>() ??
               new AzureOpenAiOptions();
    }
}
