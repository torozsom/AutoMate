using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Application.Abstractions.Ai.Results;
using Application.Ai.Context;
using Application.Ai.Policy;
using Application.Ai.Results;
using Infrastructure.Ai.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Test.Infra.Ai.Admission;
using Test.Infra.Ai.ContextAndResults;
using Test.Infra.TestSupport;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Infra.Ai.Providers;

/// <summary>
///     Azure-specific route/key/error checks plus the full shared Responses protocol contract.
/// </summary>
/// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
public sealed class AzureOpenAiAnalysisProviderTests() : ResponsesAnalysisProviderContractTests(true)
{
    /// <summary>Firewall denials retain a fixed reason without retaining provider prose or secrets.</summary>
    [Theory]
    [InlineData("Access denied due to Virtual Network/Firewall rules. password=private", "network_access_denied")]
    [InlineData("Traffic is not from an approved private endpoint.", "network_access_denied")]
    [InlineData(
        "A Virtual Network is configured for this resource. Please use the correct endpoint for making requests. Check https://aka.ms/cogsvc-vnet for more details. password=private",
        "network_access_denied")]
    [InlineData("a virtual network is configured for this resource. password=private", "network_access_denied")]
    [InlineData("The resource has been temporarily blocked.", "resource_blocked")]
    [InlineData("private arbitrary provider prose", "unknown")]
    public async Task Azure_denial_keeps_only_safe_classification(string message, string reason)
    {
        var calls = 0;
        using var handler = new DelegateHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = "403", message } }))
            };
        });
        using var client = new HttpClient(handler);
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: "azure-openai",
            endpoint: "https://automate-test.openai.azure.com/openai/v1/");
        var error = await Assert.ThrowsAsync<AnalysisProviderAccessDeniedException>(() =>
            CreateProvider(client, Configuration(), settings).AnalyzeAsync(Request()));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("403", error.Code);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("private", error.ToString());
        Assert.DoesNotContain(message, error.ToString());
        Assert.Null(error.InnerException);
    }

    /// <summary>
    ///     Rejected Azure envelopes retain only finite classifications and never provider prose.
    /// </summary>
    /// <param name="kind">Synthetic operation or response kind.</param>
    /// <param name="reason">Expected finite rejection or failure reason.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("incomplete", AnalysisResultRejection.IncompleteOutput)]
    [InlineData("refusal", AnalysisResultRejection.Refusal)]
    [InlineData("json", AnalysisResultRejection.MalformedJson)]
    [InlineData("envelope", AnalysisResultRejection.MalformedEnvelope)]
    [InlineData("schema", AnalysisResultRejection.SchemaMismatch)]
    [InlineData("limits", AnalysisResultRejection.FieldLimits)]
    [InlineData("evidence", AnalysisResultRejection.UnknownEvidenceReference)]
    [InlineData("provenance", AnalysisResultRejection.InvalidProvenance)]
    public async Task Invalid_results_have_finite_safe_reasons(string kind, AnalysisResultRejection reason)
    {
        var answer = kind switch
        {
            "schema" => "{\"overview\":\"private-value\"}",
            "json" => "{private-value",
            _ => JsonSerializer.Serialize(new
            {
                overview = "Observed operation.",
                observations = kind == "limits" ? Enumerable.Repeat("Observed.", 7).ToArray() : Array.Empty<string>(),
                metricsAssessment = Array.Empty<string>(),
                potentialIssues = Array.Empty<string>(),
                recommendedSteps = Array.Empty<string>(),
                limitations = Array.Empty<string>(),
                evidenceReferences = kind == "evidence" ? new[] { "private-value" } : Array.Empty<string>()
            })
        };
        var envelope = JsonSerializer.Serialize(new
        {
            status = kind == "incomplete" ? "incomplete" : "completed",
            model = kind == "provenance" ? "https://private-value" : "gpt-5.4-mini",
            output = new[]
            {
                new
                {
                    type = "message", role = "assistant", status = "completed",
                    content = new[] { new { type = kind == "refusal" ? "refusal" : "output_text", text = answer } }
                }
            }
        });
        using var handler = new DelegateHttpMessageHandler(_ => DelegateHttpMessageHandler.Json(
            kind == "envelope" ? "{\"private-value\":true}" : envelope));
        using var client = new HttpClient(handler);
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: "azure-openai",
            endpoint: "https://automate-test.openai.azure.com/openai/v1/");
        var error = await Assert.ThrowsAsync<InvalidAnalysisResultException>(() =>
            CreateProvider(client, Configuration(), settings)
                .AnalyzeAsync(Request() with { Kind = AssessmentKind.RuntimeOverview }));
        Assert.Equal(reason, error.Reason);
        Assert.NotNull(InvalidAnalysisResultException.Guidance(error.FailureCode));
        Assert.DoesNotContain("private-value", error.ToString());
    }

    /// <summary>
    ///     Azure-supported descriptions teach strict server limits without unsupported schema keywords.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task V2_empty_sections_are_valid_and_request_documents_server_limits()
    {
        using var handler = new DelegateHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var instructions = body.RootElement.GetProperty("instructions").GetString()!;
            Assert.Contains("6 items of 768 characters", instructions);
            Assert.Contains("identifiers exactly", instructions);
            var schema = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").ToString();
            Assert.DoesNotContain("maxLength", schema);
            Assert.DoesNotContain("maxItems", schema);
            return DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                status = "completed",
                model = "gpt-5.4-mini",
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
                                    "{\"overview\":\"\",\"observations\":[],\"metricsAssessment\":[],\"potentialIssues\":[],\"recommendedSteps\":[],\"limitations\":[],\"evidenceReferences\":[]}"
                            }
                        }
                    }
                }
            }));
        });
        using var client = new HttpClient(handler);
        var settings = AnalysisEgressPolicyTests.Approved(Guid.NewGuid(), provider: "azure-openai",
            endpoint: "https://automate-test.openai.azure.com/openai/v1/");
        var result = await CreateProvider(client, Configuration(), settings)
            .AnalyzeAsync(Request() with { Kind = AssessmentKind.RuntimeOverview });
        Assert.Equal(2, result.ResultSchemaVersion);
        Assert.Empty(result.Sections!.Observations);
        Assert.Equal("No overview was recorded for the selected evidence.", result.Summary);
    }

    /// <summary>
    ///     Azure sends the deployment alias and preserves the returned model identity separately.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Nested key/resource reload cancels headers/body I/O and makes the following call
    ///     unavailable.
    /// </summary>
    /// <param name="readingBody">Whether to block response-body reading instead of the HTTP-header phase.</param>
    /// <param name="setting">Option key overridden by the scenario.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Exact resource matching rejects credential-bearing, proxy, alternate-port and noncanonical
    ///     URLs.
    /// </summary>
    /// <param name="endpoint">Parsed HTTP(S) collector endpoint, or null when invalid or absent.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     A direct OpenAI key cannot satisfy missing/unsafe Azure credentials or an invalid resource
    ///     identity.
    /// </summary>
    /// <param name="key">Fixed connection-status query key.</param>
    /// <param name="resource">Recorded resource name for these metric samples.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Azure numeric rate-limit codes require a server retry hint; unknown/quota envelopes remain
    ///     terminal.
    /// </summary>
    /// <param name="code">Synthetic fixed provider code or HTTP status representation.</param>
    /// <param name="hint">Whether the request supplies the optional provider hint.</param>
    /// <param name="transient">Whether to inject a transient failure.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Creates isolated protected configuration with a deliberately different direct-provider key.
    /// </summary>
    /// <param name="key">Fixed connection-status query key.</param>
    /// <param name="resource">Recorded resource name for these metric samples.</param>
    /// <returns>The configured iconfiguration root instance or response for this scenario.</returns>
    private static IConfigurationRoot Configuration(string? key = "test-key", string? resource = "automate-test")
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAnalysis:ApiKey"] = "direct-key-must-not-be-used",
            ["AiAnalysis:AzureOpenAi:ApiKey"] = key,
            ["AiAnalysis:AzureOpenAi:ResourceName"] = resource
        }).Build();
    }

    /// <summary>
    ///     Constructs the real Azure adapter with synthetic transport and explicit metadata
    ///     authorization.
    /// </summary>
    /// <param name="client">Fixture HTTP or Docker client.</param>
    /// <param name="configuration">Deployment settings or host configuration used by this operation.</param>
    /// <param name="settings">Synthetic provider or telemetry settings, optionally using fixture defaults.</param>
    /// <returns>The configured Azure open ai analysis provider instance or response for this scenario.</returns>
    private static AzureOpenAiAnalysisProvider CreateProvider(HttpClient client, IConfiguration configuration,
        AiAnalysisOptions settings)
    {
        return new AzureOpenAiAnalysisProvider(client, configuration, new AnalysisEgressPolicyTests.Monitor(settings),
            AnalysisResultTests.Validator(), new DiagnosticRedactor(), new AnalysisEgressPolicyTests.Authorizer());
    }

    /// <summary>
    ///     Produces a scoped request without reading deployment data.
    /// </summary>
    /// <returns>The configured llm analysis request instance or response for this scenario.</returns>
    private static LlmAnalysisRequest Request()
    {
        return new LlmAnalysisRequest("Build failed.", [], Guid.NewGuid());
    }
}
