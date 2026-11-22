using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Ai.Policy;
using Xunit;

namespace Tests.Ai.Policy;

/// <summary>
///     Verifies fail-closed regional egress policy without providers or shared integration fixtures.
/// </summary>
public sealed class AnalysisEgressPolicyTests
{
    /// <summary>
    ///     Explicit null approval lists from configuration deny egress without throwing.
    /// </summary>
    /// <param name="property">Approval collection replaced with null.</param>
    [Theory]
    [InlineData("ApprovedRegions")]
    [InlineData("ApprovedTenantIds")]
    [InlineData("AllowedDataCategories")]
    public void Null_approval_collections_fail_closed(string property)
    {
        var configuration = JsonNode.Parse(
            JsonSerializer.Serialize(Approved(Guid.NewGuid())))!.AsObject();
        configuration[property] = null;
        var options = JsonSerializer.Deserialize<AiAnalysisOptions>(configuration.ToJsonString())!;

        Assert.False(AnalysisEgressPolicy.IsConfigured(options));
    }

    /// <summary>
    ///     Global, mismatched, insecure and credential-bearing routes never qualify for approved
    ///     egress.
    /// </summary>
    /// <param name="endpoint">Provider endpoint whose approval is checked.</param>
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

    /// <summary>
    ///     Both supported processing geographies require explicit matching configuration.
    /// </summary>
    /// <param name="region">Explicitly approved processing geography.</param>
    [Theory]
    [InlineData("eu")]
    [InlineData("us")]
    public void Explicit_regional_approvals_are_accepted(string region)
    {
        Assert.True(AnalysisEgressPolicy.IsConfigured(Approved(Guid.NewGuid(),
            endpoint: $"https://{region}.api.openai.com/v1/", region: region)));
    }

    /// <summary>
    ///     Default options, empty tenants and incomplete/unknown categories never grant permission.
    /// </summary>
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

    /// <summary>
    ///     Creates a fully approved snapshot so each denial scenario changes only its relevant policy input.
    /// </summary>
    /// <param name="tenant">Synthetic account included in the approval list.</param>
    /// <param name="enabled">Whether analysis is enabled.</param>
    /// <param name="endpoint">Regional provider endpoint under test.</param>
    /// <param name="region">Selected processing geography.</param>
    /// <param name="categories">Approved evidence categories, or the supported defaults.</param>
    /// <param name="egressEnabled">Whether outbound provider processing is enabled.</param>
    /// <param name="regionalApproval">Whether regional processing has explicit approval.</param>
    /// <param name="provider">Configured provider identity.</param>
    /// <param name="approvedRegions">Approved geographies, or both supported regions.</param>
    /// <param name="retentionDays">Result retention limit in days.</param>
    /// <param name="model">Configured model identifier.</param>
    /// <returns>An independent synthetic policy snapshot.</returns>
    private static AiAnalysisOptions Approved(Guid tenant, bool enabled = true,
        string endpoint = "https://eu.api.openai.com/v1/", string region = "eu", string[]? categories = null,
        bool egressEnabled = true, bool regionalApproval = true, string? provider = "openai",
        string[]? approvedRegions = null, int retentionDays = 90, string model = "gpt-5-mini")
    {
        return new AiAnalysisOptions
        {
            Enabled = enabled,
            ProviderEgressEnabled = egressEnabled,
            Provider = provider,
            RegionalProcessingApproved = regionalApproval,
            ProcessingRegion = region,
            ApprovedRegions = approvedRegions ?? ["eu", "us"],
            ApprovedTenantIds = [tenant],
            AllowedDataCategories = categories ?? ["logs", "metrics", "traceCorrelation"],
            Endpoint = endpoint,
            ResultRetentionDays = retentionDays,
            Model = model
        };
    }
}
