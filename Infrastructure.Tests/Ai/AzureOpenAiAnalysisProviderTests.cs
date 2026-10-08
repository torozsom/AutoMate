using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Ai;
using Infrastructure.Ai;
using Infrastructure.Diagnostics;
using Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Ai;

/// <summary>Azure-specific route/key/error checks plus the full shared Responses protocol contract.</summary>
public sealed class AzureOpenAiAnalysisProviderTests() : ResponsesAnalysisProviderContractTests(true)
{
    /// <summary>Azure sends the deployment alias and preserves the returned model identity separately.</summary>
    [Fact]
    public async Task Deployment_alias_and_actual_model_have_distinct_provenance()
    {
        using var handler = new DelegateHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("analysis-deployment", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("api-key")));
            return DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                status = "completed",
                model = "gpt-4.1-mini-2025-04-14",
                output = new[]
                {
                    new
                    {
                        type = "message", role = "assistant", status = "completed",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text =
                                    """{"summary":"Check configuration.","recommendedSteps":[],"evidenceReferences":[]}"""
                            }
                        }
                    }
                }
            }));
        });
        using var client = new HttpClient(handler);
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: "azure-openai",
            endpoint: "https://automate-test.openai.azure.com/openai/v1/", model: "analysis-deployment");
        var result = await CreateProvider(client, Configuration(), settings).AnalyzeAsync(Request());
        Assert.Equal("azure-openai", result.Provider);
        Assert.Equal("analysis-deployment", result.RequestedModel);
        Assert.Equal("gpt-4.1-mini-2025-04-14", result.Model);
        Assert.Null(result.EstimatedCost);
    }

    /// <summary>Nested key/resource reload cancels headers/body I/O and makes the following call unavailable.</summary>
    [Theory]
    [InlineData(false, "ApiKey")]
    [InlineData(true, "ApiKey")]
    [InlineData(false, "ResourceName")]
    [InlineData(true, "ResourceName")]
    public async Task Azure_configuration_reload_cancels_active_request(bool readingBody, string setting)
    {
        var configuration = Configuration();
        configuration["AiAnalysis:Enabled"] = "true";
        configuration["AiAnalysis:ProviderEgressEnabled"] = "true";
        configuration["AiAnalysis:Provider"] = "azure-openai";
        configuration["AiAnalysis:RegionalProcessingApproved"] = "true";
        configuration["AiAnalysis:ProcessingRegion"] = "eu";
        configuration["AiAnalysis:ApprovedRegions:0"] = "eu";
        configuration["AiAnalysis:ApprovedTenantIds:0"] = Guid.NewGuid().ToString();
        configuration["AiAnalysis:AllowedDataCategories:0"] = "logs";
        configuration["AiAnalysis:AllowedDataCategories:1"] = "metrics";
        configuration["AiAnalysis:AllowedDataCategories:2"] = "traceCorrelation";
        configuration["AiAnalysis:Endpoint"] = "https://automate-test.openai.azure.com/openai/v1/";
        var services = new ServiceCollection();
        services.AddOptions<AiAnalysisOptions>().Bind(configuration.GetSection("AiAnalysis"));
        using var container = services.BuildServiceProvider();
        var monitor = container.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ShutdownHandler(started, readingBody);
        using var client = new HttpClient(handler);
        var provider = new AzureOpenAiAnalysisProvider(client, configuration, monitor, AnalysisResultTests.Validator(),
            new DiagnosticRedactor(), new AnalysisEgressPolicyTests.Authorizer());
        var active = provider.AnalyzeAsync(Request());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        configuration[$"AiAnalysis:AzureOpenAi:{setting}"] = "";
        configuration.Reload();
        await Assert.ThrowsAsync<AnalysisProviderUnavailableException>(async () =>
            await active.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<AnalysisProviderUnavailableException>(() => provider.AnalyzeAsync(Request()));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Exact resource matching rejects credential-bearing, proxy, alternate-port and noncanonical URLs.</summary>
    [Theory]
    [InlineData("https://other.openai.azure.com/openai/v1/")]
    [InlineData("http://automate-test.openai.azure.com/openai/v1/")]
    [InlineData("https://automate-test.openai.azure.com:443/openai/v1/")]
    [InlineData("https://key@automate-test.openai.azure.com/openai/v1/")]
    [InlineData("https://automate-test.openai.azure.com/openai/v1/?secret=value")]
    [InlineData("https://automate-test.openai.azure.com/openai/v1/#fragment")]
    [InlineData("https://automate-test.openai.azure.com/openai/v1")]
    [InlineData("https://automate-test.openai.azure.com/openai/v1/../v1/")]
    [InlineData("https://automate-test.openai.azure.com.evil.invalid/openai/v1/")]
    [InlineData("https://automate-test.services.ai.azure.com/openai/v1/")]
    public async Task Unapproved_routes_never_send(string endpoint)
    {
        using var handler = new DelegateHttpMessageHandler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: "azure-openai", endpoint: endpoint);
        var provider = CreateProvider(client, Configuration(), settings);
        await Assert.ThrowsAsync<AnalysisProviderUnavailableException>(() => provider.AnalyzeAsync(Request()));
    }

    /// <summary>A direct OpenAI key cannot satisfy missing/unsafe Azure credentials or an invalid resource identity.</summary>
    [Theory]
    [InlineData(null, "automate-test")]
    [InlineData("", "automate-test")]
    [InlineData("bad\r\nheader", "automate-test")]
    [InlineData("test-key", null)]
    [InlineData("test-key", "-automate-test")]
    [InlineData("test-key", "automate.test")]
    [InlineData("test-key", "automate-test/other")]
    public async Task Missing_or_invalid_Azure_configuration_has_no_fallback(string? key, string? resource)
    {
        using var handler = new DelegateHttpMessageHandler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var configuration = Configuration(key, resource);
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: "azure-openai",
            endpoint: "https://automate-test.openai.azure.com/openai/v1/");
        await Assert.ThrowsAsync<AnalysisProviderUnavailableException>(() =>
            CreateProvider(client, configuration, settings).AnalyzeAsync(Request()));
        Assert.False(AzureOpenAiAnalysisProvider.ReadOptions(configuration).HasApiKey() &&
                     AzureOpenAiAnalysisProvider.ReadOptions(configuration).ApprovesRoute(settings));
    }

    /// <summary>Azure numeric rate-limit codes require a server retry hint; unknown/quota envelopes remain terminal.</summary>
    [Theory]
    [InlineData("429", true, true)]
    [InlineData("429", false, false)]
    [InlineData("insufficient_quota", true, false)]
    [InlineData("unknown", true, false)]
    public async Task Azure_rate_limit_hints_are_classified_without_inline_retries(string code, bool hint,
        bool transient)
    {
        var calls = 0;
        using var handler = new DelegateHttpMessageHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                    { error = new { code, message = "password=private-value" } }))
            };
            if (hint) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });
        using var client = new HttpClient(handler);
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: "azure-openai",
            endpoint: "https://automate-test.openai.azure.com/openai/v1/");
        var error = await Record.ExceptionAsync(() =>
            CreateProvider(client, Configuration(), settings).AnalyzeAsync(Request()));
        if (transient) Assert.Equal(42, Assert.IsType<TransientAnalysisProviderException>(error).RetryAfterSeconds);
        else Assert.IsType<HttpRequestException>(error);
        Assert.DoesNotContain("private-value", error!.ToString());
        Assert.Equal(1, calls);
    }

    /// <summary>Creates isolated protected configuration with a deliberately different direct-provider key.</summary>
    private static IConfigurationRoot Configuration(string? key = "test-key", string? resource = "automate-test")
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAnalysis:ApiKey"] = "direct-key-must-not-be-used",
            ["AiAnalysis:AzureOpenAi:ApiKey"] = key,
            ["AiAnalysis:AzureOpenAi:ResourceName"] = resource
        }).Build();
    }

    /// <summary>Constructs the real Azure adapter with synthetic transport and explicit metadata authorization.</summary>
    private static AzureOpenAiAnalysisProvider CreateProvider(HttpClient client, IConfiguration configuration,
        AiAnalysisOptions settings)
    {
        return new AzureOpenAiAnalysisProvider(client, configuration, new AnalysisEgressPolicyTests.Monitor(settings),
            AnalysisResultTests.Validator(), new DiagnosticRedactor(), new AnalysisEgressPolicyTests.Authorizer());
    }

    /// <summary>Produces a scoped request without reading deployment data.</summary>
    private static LlmAnalysisRequest Request()
    {
        return new LlmAnalysisRequest("Build failed.", [], Guid.NewGuid());
    }
}