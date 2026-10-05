using Application.Abstractions.Ai;
using Application.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>
///     Composition-root adapter identity, implementation type and exact approved-route policy; never contains
///     credentials.
/// </summary>
/// <param name="Name">Canonical ordinal provider identifier.</param>
/// <param name="ImplementationType">Concrete provider port implementation registered in the current scope.</param>
/// <param name="ApprovesRoute">Adapter-specific exact endpoint/region approval, in addition to shared policy.</param>
public sealed record AnalysisProviderRegistration(
    string Name,
    Type ImplementationType,
    Func<AiAnalysisOptions, bool> ApprovesRoute);

/// <summary>Immutable adapter catalog separates provider selection and route approval from the workflow/UI.</summary>
public sealed class AnalysisProviderCatalog
{
    /// <summary>Ordinal canonical provider registrations, immutable after composition.</summary>
    private readonly IReadOnlyDictionary<string, AnalysisProviderRegistration> _providers;

    /// <summary>Rejects ambiguous names or implementations that do not satisfy the provider port.</summary>
    public AnalysisProviderCatalog(IEnumerable<AnalysisProviderRegistration> registrations)
    {
        var entries = registrations.ToArray();
        if (entries.Any(item => item.Name.Length is < 1 or > 100 ||
                                !item.Name.All(character =>
                                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
                                !typeof(ILlmAnalysisProvider).IsAssignableFrom(item.ImplementationType) ||
                                item.ApprovesRoute is null))
            throw new ArgumentException("Invalid analysis provider registration.", nameof(registrations));
        _providers = entries.ToDictionary(item => item.Name, StringComparer.Ordinal);
    }

    /// <summary>Unknown, unapproved or incompletely configured providers are unavailable; no adapter is instantiated.</summary>
    public AnalysisProviderRegistration? Select(AiAnalysisOptions settings)
    {
        return AnalysisEgressPolicy.IsCommonConfigured(settings) && settings.Provider is { } name &&
               _providers.TryGetValue(name, out var provider) && provider.ApprovesRoute(settings)
            ? provider
            : null;
    }

    /// <summary>Used at startup and fresh metadata egress checks; route approval never grants tenant consent.</summary>
    public bool IsConfigured(AiAnalysisOptions settings)
    {
        return Select(settings) is not null;
    }
}

/// <summary>
///     Scoped provider-neutral router; selection follows current typed options without changing worker or UI
///     contracts.
/// </summary>
public sealed class ConfiguredAnalysisProvider(
    IServiceProvider services,
    AnalysisProviderCatalog catalog,
    IOptionsMonitor<AiAnalysisOptions> options) : ILlmAnalysisProvider
{
    /// <inheritdoc />
    public async Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.CurrentValue;
        var selected = catalog.Select(settings) ?? throw new AnalysisProviderUnavailableException();
        var provider = (ILlmAnalysisProvider)services.GetRequiredService(selected.ImplementationType);
        if (!ReferenceEquals(settings, options.CurrentValue)) throw new AnalysisProviderUnavailableException();
        var response = await provider.AnalyzeAsync(request, cancellationToken);
        if (!string.Equals(response.Provider, selected.Name, StringComparison.Ordinal))
            throw new InvalidAnalysisResultException();
        return response;
    }
}