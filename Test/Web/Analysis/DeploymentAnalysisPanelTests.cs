using System.Text.RegularExpressions;
using Application.Abstractions.Ai.Execution;
using Application.Ai.Results;
using Domain.Enums.Ai;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Web.Components.Shared;

namespace Test.Web.Analysis;

/// <summary>
///     Renders actual analysis markup to verify state guidance, safe text, stale-result exclusion
///     and owner-action eligibility.
/// </summary>
public sealed class DeploymentAnalysisPanelTests
{
    /// <summary>
    ///     A provider denial explains the external correction without exposing arbitrary provider text.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Provider_denial_has_actionable_fixed_guidance()
    {
        var result = Result(AiAnalysisStatus.Failed) with
        {
            FailureCode = AnalysisProviderAccessDeniedException.FailureCode
        };
        var html = await RenderAsync(result);

        Assert.Contains("provider denied access", html);
        Assert.Contains("network access", html);
        Assert.DoesNotContain("private", html);
    }

    /// <summary>
    ///     Every durable state has an accessible textual label; nonterminal payloads are omitted.
    /// </summary>
    /// <param name="status">Analysis state under test.</param>
    /// <param name="label">Accessible label or presentation text under test.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(AiAnalysisStatus.Queued, "Queued")]
    [InlineData(AiAnalysisStatus.Running, "Processing")]
    [InlineData(AiAnalysisStatus.Completed, "Completed")]
    [InlineData(AiAnalysisStatus.Cancelled, "Canceled")]
    [InlineData(AiAnalysisStatus.Skipped, "Skipped")]
    [InlineData(AiAnalysisStatus.Failed, "Analysis failed")]
    public async Task States_render_safe_accessible_guidance(AiAnalysisStatus status, string label)
    {
        var result = Result(status);
        var html = await RenderAsync(result);

        Assert.Contains(label, html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("type=\"button\"", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Equal(status == AiAnalysisStatus.Completed, html.Contains("&lt;script&gt;", StringComparison.Ordinal));
        Assert.Equal(status == AiAnalysisStatus.Completed,
            html.Contains("aria-label=\"AI analysis answer\"", StringComparison.Ordinal));

        if (status == AiAnalysisStatus.Completed)
        {
            Assert.Contains("Recommended steps", html);
            Assert.Contains("Evidence references", html);
            Assert.Contains("order:42", html);
            Assert.Contains("gpt-5-mini", html);
        }
        else
        {
            Assert.DoesNotContain("order:42", html);
        }

        var root = FindRoot();
        var preview = Path.Combine(root, ".artifacts", "analysis-preview");

        Directory.CreateDirectory(preview);
        await File.WriteAllTextAsync(Path.Combine(preview, status + ".html"),
            "<!doctype html><html><head><meta name='viewport' content='width=device-width,initial-scale=1'><link rel='stylesheet' href='/bootstrap.css'><link rel='stylesheet' href='/app.css'><link rel='stylesheet' href='/analysis.css'></head><body><main style='max-width:1000px;margin:auto;padding:16px'><p>UI verification: synthetic analysis data</p>" +
            html + "</main></body></html>");
        File.Copy(Path.Combine(root, "Web", "Components", "Shared", "Analysis", "DeploymentAnalysisPanel.razor.css"),
            Path.Combine(preview, "analysis.css"), true);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "app.css"), Path.Combine(preview, "app.css"), true);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "lib", "bootstrap", "dist", "css", "bootstrap.min.css"),
            Path.Combine(preview, "bootstrap.css"), true);
    }

    /// <summary>
    ///     Missing consent, operator disablement or nonfailed deployment disables manual requests with
    ///     an explanation.
    /// </summary>
    /// <param name="enabled">Whether to include the option.</param>
    /// <param name="consent">Legacy preference value used to verify automatic collection behavior.</param>
    /// <param name="failed">The recorded failed-outcome count.</param>
    /// <param name="explanation">Fixed explanation expected in the rendered view.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false, true, true, "disabled by the operator")]
    [InlineData(true, false, true, "consent is not enabled")]
    [InlineData(true, true, false, "after a deployment fails")]
    public async Task Request_gates_are_visible(bool enabled, bool consent, bool failed, string explanation)
    {
        var html = await RenderAsync(null, enabled, consent, failed);

        Assert.Contains(explanation, html);
        Assert.Contains("disabled", html);
        Assert.Contains("No analysis yet", html);
    }

    /// <summary>
    ///     A result for another deployment cannot appear under the current selection.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Stale_result_is_omitted()
    {
        var html = await RenderAsync(Result(AiAnalysisStatus.Completed), deploymentId: Guid.NewGuid());

        Assert.Contains("No analysis yet", html);
        Assert.DoesNotContain("order:42", html);
        Assert.DoesNotContain("Cancel analysis", html);
    }

    /// <summary>
    ///     Cancellation remains available for active work when new requests are disabled or consent
    ///     revoked.
    /// </summary>
    /// <param name="status">Analysis state under test.</param>
    /// <param name="visible">Whether the element or result should be rendered.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(AiAnalysisStatus.Queued, true)]
    [InlineData(AiAnalysisStatus.Running, true)]
    [InlineData(AiAnalysisStatus.Completed, false)]
    [InlineData(AiAnalysisStatus.Cancelled, false)]
    public async Task Cancellation_control_matches_saved_state(AiAnalysisStatus status, bool visible)
    {
        var html = await RenderAsync(Result(status), false, false);
        // Razor source line wrapping is equivalent whitespace in visible button and paragraph text.
        html = Regex.Replace(html, @"\s+", " ");

        Assert.Equal(visible, html.Contains("Cancel analysis", StringComparison.Ordinal));
        Assert.Contains("Allow diagnostic data egress", html);
        Assert.Contains("data already sent cannot be recalled", html);
        Assert.Contains("aria-describedby", html);
    }

    /// <summary>
    ///     Quota, missing context and unavailable configuration use fixed guidance, including hostile
    ///     legacy codes.
    /// </summary>
    /// <param name="code">Synthetic fixed provider code or HTTP status representation.</param>
    /// <param name="guidance">Expected fixed customer-facing failure guidance.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("quota_exceeded", "daily analysis allowance")]
    [InlineData("unsupported_data", "no supported diagnostic data")]
    [InlineData("tenant_quota_exceeded", "account&#x27;s daily analysis allowance")]
    [InlineData("rate_limited", "analysis request rate limit")]
    [InlineData("concurrency_exceeded", "shared provider processing capacity")]
    [InlineData("budget_exceeded", "reserved spending allowance")]
    [InlineData("budget_not_configured", "Configure positive")]
    [InlineData("budget_configuration_invalid", "configuration is invalid")]
    [InlineData("budget_currency_mismatch", "reservation currency")]
    [InlineData("unavailable", "egress approval is unavailable")]
    [InlineData("private-secret", "egress approval is unavailable")]
    public async Task Skip_states_use_authored_guidance(string code, string guidance)
    {
        var html = await RenderAsync(Result(AiAnalysisStatus.Skipped) with { FailureCode = code });

        Assert.Contains(guidance, html);
        Assert.DoesNotContain("private-secret", html);
    }

    /// <summary>
    ///     Current and legacy failures show fixed guidance without framing them as successful answers.
    /// </summary>
    /// <param name="code">Synthetic fixed provider code or HTTP status representation.</param>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("invalid_response", "supported result format")]
    [InlineData("invalid_response_limits", "supported result limits")]
    [InlineData("invalid_response_evidence", "outside the selected context")]
    [InlineData("invalid_response_incomplete", "output allowance")]
    public async Task Rejected_answers_have_actionable_guidance(string code, string message)
    {
        var html = await RenderAsync(Result(AiAnalysisStatus.Failed) with { FailureCode = code });

        Assert.Contains(message, html);
        Assert.DoesNotContain("analysis-answer", html);
    }

    /// <summary>
    ///     Known synthetic safe view, with hostile HTML text to prove normal Razor encoding.
    /// </summary>
    /// <param name="status">Analysis state under test.</param>
    /// <returns>The configured deployment analysis view instance or response for this scenario.</returns>
    private static DeploymentAnalysisView Result(AiAnalysisStatus status)
    {
        return new DeploymentAnalysisView(Guid.NewGuid(), Guid.NewGuid(), status,
            AiAnalysisTrigger.Manual, "<script>alert('example')</script>", ["Review configuration"], ["order:42"],
            status == AiAnalysisStatus.Skipped ? "unsupported_data" : "unavailable", DateTimeOffset.UtcNow,
            status is AiAnalysisStatus.Queued or AiAnalysisStatus.Running ? null : DateTimeOffset.UtcNow,
            "openai", "gpt-5-mini", PromptVersion: "deployment-diagnostics-v2", ResultSchemaVersion: 1);
    }

    /// <summary>
    ///     Uses the real Razor rendering pipeline without backend or provider services.
    /// </summary>
    /// <param name="result">Configured saved analysis result, or null when absent.</param>
    /// <param name="enabled">Whether to include the option.</param>
    /// <param name="consent">Legacy preference value used to verify automatic collection behavior.</param>
    /// <param name="failed">The recorded failed-outcome count.</param>
    /// <param name="deploymentId">Synthetic deployment identity, or null when absent.</param>
    /// <returns>A task returning the synthetic text produced for this scenario.</returns>
    private static async Task<string> RenderAsync(DeploymentAnalysisView? result, bool enabled = true,
        bool consent = true,
        bool failed = true, Guid? deploymentId = null)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<DeploymentAnalysisPanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["DeploymentId"] = deploymentId ?? result?.DeploymentId ?? Guid.NewGuid(),
                    ["Result"] = result,
                    ["Enabled"] = enabled,
                    ["Consented"] = consent,
                    ["CanEditConsent"] = true,
                    ["FailedDeployment"] = failed
                }))).ToHtmlString());
    }

    /// <summary>
    ///     Locates preview output under the actual repository.
    /// </summary>
    /// <returns>The synthetic text produced for this scenario.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The fixture injects a configured failure or detects an unexpected operation.
    /// </exception>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutoMate.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }
}
