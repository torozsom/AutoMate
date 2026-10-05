using Application.Abstractions.Ai;
using Application.Ai;
using Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Tests.Ai;

/// <summary>Credential-free registry routing and fail-closed configuration tests, independent of workflow/UI changes.</summary>
public sealed class AnalysisProviderSelectionTests
{
    /// <summary>Missing/unknown/mismatched/disabled settings never instantiate or invoke a provider.</summary>
    [Theory]
    [InlineData(null, true, "https://eu.api.openai.com/v1/")]
    [InlineData("unknown", true, "https://eu.api.openai.com/v1/")]
    [InlineData("openai", false, "https://eu.api.openai.com/v1/")]
    [InlineData("openai", true, "https://api.openai.com/v1/")]
    public async Task Unavailable_selection_never_resolves_an_adapter(string? name, bool enabled, string endpoint)
    {
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: name, egressEnabled: enabled,
            endpoint: endpoint);
        var resolutions = 0;
        using var services = new ServiceCollection().AddTransient<Stub>(_ =>
        {
            resolutions++;
            return new Stub("openai");
        }).BuildServiceProvider();
        var catalog = new AnalysisProviderCatalog([Registration("openai")]);
        var router = new ConfiguredAnalysisProvider(services, catalog, new AnalysisEgressPolicyTests.Monitor(settings));
        await Assert.ThrowsAsync<AnalysisProviderUnavailableException>(() =>
            router.AnalyzeAsync(new LlmAnalysisRequest("safe")));
        Assert.Equal(0, resolutions);
    }

    /// <summary>
    ///     An additional registered adapter and exact route policy are selected without changing worker or port
    ///     contracts.
    /// </summary>
    [Fact]
    public async Task Registered_adapter_selection_follows_current_typed_options()
    {
        var owner = Guid.NewGuid();
        var monitor =
            new AnalysisEgressPolicyTests.Monitor(AnalysisEgressPolicyTests.Approved(owner, provider: "synthetic"));
        using var services = new ServiceCollection().AddTransient<Stub>(_ => new Stub("synthetic"))
            .BuildServiceProvider();
        var catalog = new AnalysisProviderCatalog([Registration("openai"), Registration("synthetic")]);
        var router = new ConfiguredAnalysisProvider(services, catalog, monitor);
        Assert.Equal("synthetic", (await router.AnalyzeAsync(new LlmAnalysisRequest("safe"))).Provider);
        monitor.CurrentValue = AnalysisEgressPolicyTests.Approved(owner, provider: "unknown");
        await Assert.ThrowsAsync<AnalysisProviderUnavailableException>(() =>
            router.AnalyzeAsync(new LlmAnalysisRequest("safe")));
    }

    /// <summary>A policy reload while resolving the adapter fails closed before its invocation.</summary>
    [Fact]
    public async Task Reload_during_resolution_denies_invocation()
    {
        var owner = Guid.NewGuid();
        var monitor = new AnalysisEgressPolicyTests.Monitor(AnalysisEgressPolicyTests.Approved(owner));
        var stub = new Stub("openai");
        using var services = new ServiceCollection().AddTransient<Stub>(_ =>
        {
            monitor.CurrentValue = AnalysisEgressPolicyTests.Approved(owner, egressEnabled: false);
            return stub;
        }).BuildServiceProvider();
        var router =
            new ConfiguredAnalysisProvider(services, new AnalysisProviderCatalog([Registration("openai")]), monitor);
        await Assert.ThrowsAsync<AnalysisProviderUnavailableException>(() =>
            router.AnalyzeAsync(new LlmAnalysisRequest("safe")));
        Assert.Equal(0, stub.Calls);
    }

    /// <summary>Adapters cannot claim another provider identity in their result provenance.</summary>
    [Fact]
    public async Task Mismatched_provider_provenance_is_rejected()
    {
        var monitor = new AnalysisEgressPolicyTests.Monitor(AnalysisEgressPolicyTests.Approved(Guid.NewGuid()));
        using var services = new ServiceCollection().AddTransient<Stub>(_ => new Stub("different"))
            .BuildServiceProvider();
        var router =
            new ConfiguredAnalysisProvider(services, new AnalysisProviderCatalog([Registration("openai")]), monitor);
        await Assert.ThrowsAsync<InvalidAnalysisResultException>(() =>
            router.AnalyzeAsync(new LlmAnalysisRequest("safe")));
    }

    /// <summary>Ambiguous names fail composition instead of silently choosing an adapter.</summary>
    [Fact]
    public void Duplicate_provider_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new AnalysisProviderCatalog([Registration("openai"), Registration("openai")]));
    }

    /// <summary>Provides a synthetic route approval only; never contacts the endpoint.</summary>
    private static AnalysisProviderRegistration Registration(string name)
    {
        return new AnalysisProviderRegistration(name, typeof(Stub),
            settings => settings.Endpoint == $"https://{settings.ProcessingRegion}.api.openai.com/v1/");
    }

    /// <summary>Records provider invocation without network activity.</summary>
    private sealed class Stub(string name) : ILlmAnalysisProvider
    {
        /// <summary>Number of calls for fail-closed assertions.</summary>
        public int Calls { get; private set; }

        /// <inheritdoc />
        public Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LlmAnalysisResponse(name, "test-model", "Check startup configuration.", [], []));
        }
    }
}