using Application.Abstractions.Ai;
using Application.Ai;
using Domain.Enums;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Ai;

/// <summary>Credential-free approval fixtures and fail-closed geography/category policy coverage.</summary>
public sealed class AnalysisEgressPolicyTests
{
    /// <summary>Creates explicit synthetic approvals; these never enable any deployed environment.</summary>
    internal static AiAnalysisOptions Approved(Guid tenant, bool enabled = true,
        string endpoint = "https://eu.api.openai.com/v1/",
        string region = "eu", string[]? categories = null, bool egressEnabled = true,
        bool regionalApproval = true, string? provider = "openai", string[]? approvedRegions = null,
        int retentionDays = 90, string model = "gpt-5-mini", int dailyLimit = 5, bool automatic = false,
        int concurrency = 1)
    {
        return new AiAnalysisOptions
        {
            Enabled = enabled,
            AutomaticAnalysisEnabled = automatic,
            MaximumConcurrency = concurrency,
            ProviderEgressEnabled = egressEnabled,
            Provider = provider,
            RegionalProcessingApproved = regionalApproval,
            ProcessingRegion = region,
            ApprovedRegions = approvedRegions ?? ["eu", "us"],
            ApprovedTenantIds = [tenant],
            AllowedDataCategories = categories ?? ["logs", "metrics", "traceCorrelation"],
            Endpoint = endpoint,
            ResultRetentionDays = retentionDays,
            DailyProjectLimit = dailyLimit,
            Model = model
        };
    }

    /// <summary>Global, mismatched, insecure and credential-bearing routes never qualify for approved egress.</summary>
    [Theory]
    [InlineData("https://api.openai.com/v1/")]
    [InlineData("https://us.api.openai.com/v1/")]
    [InlineData("http://eu.api.openai.com/v1/")]
    [InlineData("https://eu.api.openai.com:443/v1/")]
    [InlineData("https://key@eu.api.openai.com/v1/")]
    [InlineData("https://eu.api.openai.com/v1/?region=eu")]
    [InlineData("https://eu.api.openai.com/v1/#fragment")]
    [InlineData("https://example.invalid/v1/")]
    public void Unapproved_routes_are_denied(string endpoint)
    {
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), endpoint: endpoint)));
    }

    /// <summary>Both supported processing geographies require explicit matching configuration.</summary>
    [Theory]
    [InlineData("eu")]
    [InlineData("us")]
    public void Explicit_regional_approvals_are_accepted(string region)
    {
        Assert.True(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(),
            endpoint: $"https://{region}.api.openai.com/v1/", region: region)));
    }

    /// <summary>Default options, empty tenants and incomplete/unknown categories never grant permission.</summary>
    [Fact]
    public void Defaults_and_missing_approvals_are_denied()
    {
        Assert.False(AnalysisEgressPolicy.IsConfigured(new AiAnalysisOptions()));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), model: "password=private-value")));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), model: new string('x', 129))));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), egressEnabled: false)));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), regionalApproval: false)));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), provider: null)));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), provider: "unknown")));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), approvedRegions: [])));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), retentionDays: 91)));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.Empty)));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), false)));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(), categories: ["logs"])));
        Assert.False(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(),
            categories: ["logs", "metrics", "credentials"])));
    }

    /// <summary>Reloadable synthetic options used to test operator kill switches without deployed configuration.</summary>
    internal sealed class Monitor(AiAnalysisOptions value) : IOptionsMonitor<AiAnalysisOptions>
    {
        /// <summary>Current test policy snapshot.</summary>
        public AiAnalysisOptions CurrentValue { get; set; } = value;

        /// <inheritdoc />
        public AiAnalysisOptions Get(string? name)
        {
            return CurrentValue;
        }

        /// <inheritdoc />
        public IDisposable? OnChange(Action<AiAnalysisOptions, string?> listener)
        {
            return null;
        }
    }

    /// <summary>Provider fixture authorization is explicit and never used by production registration.</summary>
    internal sealed class Authorizer(bool allowed = true, Action? onCheck = null) : IAnalysisEgressAuthorizer
    {
        /// <inheritdoc />
        public Task<bool> AuthorizeAsync(Guid deploymentId, AiAnalysisTrigger trigger,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onCheck?.Invoke();
            return Task.FromResult(allowed && deploymentId != Guid.Empty);
        }
    }
}