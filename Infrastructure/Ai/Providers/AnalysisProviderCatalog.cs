using Application.Abstractions.Ai.Results;
using Application.Ai.Policy;
using Application.Ai.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai.Providers;

/// <summary>
///     Composition-root adapter identity, implementation type and exact approved-route policy;
///     never contains credentials.
/// </summary>
/// <param name="Name">Canonical ordinal provider identifier.</param>
/// <param name="ImplementationType">
///     Concrete provider port implementation registered in the current scope.
/// </param>
/// <param name="ApprovesRoute">
///     Adapter-specific exact endpoint/region approval, in addition to shared policy.
/// </param>
/// <param name="CredentialsConfigured">
///     Optional local credential-presence check; must never resolve an adapter or perform I/O.
/// </param>
public sealed record AnalysisProviderRegistration(
    string Name,
    Type ImplementationType,
    Func<AiAnalysisOptions, bool> ApprovesRoute,
    Func<bool>? CredentialsConfigured = null);

/// <summary>
///     Immutable adapter catalog separates provider selection and route approval from the
///     workflow/UI.
/// </summary>
public sealed class AnalysisProviderCatalog
{
    /// <summary>Ordinal canonical provider registrations, immutable after composition.</summary>
    private readonly Dictionary<string, AnalysisProviderRegistration> _providers;

    /// <summary>
    ///     Rejects ambiguous names or implementations that do not satisfy the provider port.
    /// </summary>
    /// <param name="registrations">Allowlisted provider registrations to validate.</param>
    /// <exception cref="ArgumentException">Invalid analysis provider registration.</exception>
    public AnalysisProviderCatalog(IEnumerable<AnalysisProviderRegistration> registrations)
    {
        var entries = registrations.ToArray();
        if (entries.Any(item =>
                item.Name.Length is < 1 or > 100
                || !item.Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || !typeof(ILlmAnalysisProvider).IsAssignableFrom(item.ImplementationType) ||
                item is { ApprovesRoute: null }))
            throw new ArgumentException("Invalid analysis provider registration.", nameof(registrations));

        _providers = entries.ToDictionary(item => item.Name, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Unknown, unapproved or incompletely configured providers are unavailable; no adapter is
    ///     instantiated.
    /// </summary>
    /// <param name="settings">The analysis options whose bounds are validated.</param>
    /// <returns>
    ///     The analysis provider registration projection derived from the verified inputs, or null
    ///     when no value is available.
    /// </returns>
    public AnalysisProviderRegistration? Select(AiAnalysisOptions settings)
    {
        return AnalysisEgressPolicy.IsCommonConfigured(settings)
               && settings.Provider is { } name
               && _providers.TryGetValue(name, out var provider)
               && provider.ApprovesRoute(settings)
            ? provider
            : null;
    }

    /// <summary>
    ///     Used at startup and fresh metadata egress checks; route approval never grants tenant
    ///     consent.
    /// </summary>
    /// <param name="settings">The analysis options whose bounds are validated.</param>
    /// <returns>Whether the documented configured condition holds.</returns>
    public bool IsConfigured(AiAnalysisOptions settings)
    {
        return Select(settings) is not null;
    }
}

/// <summary>
///     Scoped provider-neutral router; selection follows current typed options without changing
///     worker or UI contracts.
/// </summary>
/// <param name="services">Resolves the current scoped operation dependencies.</param>
/// <param name="catalog">Allowlisted provider registry used for adapter selection.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
public sealed class ConfiguredAnalysisProvider(
    IServiceProvider services,
    AnalysisProviderCatalog catalog,
    IOptionsMonitor<AiAnalysisOptions> options) : ILlmAnalysisProvider
{
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
    /// <exception cref="AnalysisProviderUnavailableException">
    ///     Current provider selection or returned provenance violates the configured contract.
    /// </exception>
    /// <exception cref="InvalidAnalysisResultException">
    ///     Current provider selection or returned provenance violates the configured contract.
    /// </exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    /// <exception cref="T:AnalysisProviderAccessDeniedException">
    ///     The provider rejects access to the configured resource or model.
    /// </exception>
    /// <exception cref="T:TransientAnalysisProviderException">
    ///     The provider or transport reports a failure eligible for bounded retry handling.
    /// </exception>
    /// <exception cref="T:System.ArgumentException">
    ///     The supplied context exceeds configured input limits.
    /// </exception>
    /// <exception cref="T:System.Net.Http.HttpRequestException">
    ///     The provider reports a permanent HTTP failure.
    /// </exception>
    public async Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = options.CurrentValue;
        var selected = catalog.Select(settings) ?? throw new AnalysisProviderUnavailableException();
        var provider = (ILlmAnalysisProvider)services.GetRequiredService(selected.ImplementationType);

        if (!ReferenceEquals(settings, options.CurrentValue)) throw new AnalysisProviderUnavailableException();
        var response = await provider.AnalyzeAsync(request, cancellationToken);

        return !string.Equals(response.Provider, selected.Name, StringComparison.Ordinal)
            ? throw new InvalidAnalysisResultException()
            : response;
    }
}
