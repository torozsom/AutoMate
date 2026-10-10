using System.Reflection;
using System.Text.RegularExpressions;
using Application.Abstractions.Ai;
using Application.Ai;
using Domain.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>
///     Renders actual analysis markup to verify state guidance, safe text, stale-result exclusion and owner-action
///     eligibility.
/// </summary>
public sealed class DeploymentAnalysisPanelTests
{
    /// <summary>A provider denial explains the external correction without exposing arbitrary provider text.</summary>
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

    /// <summary>Every durable state has an accessible textual label; nonterminal payloads are omitted.</summary>
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
        File.Copy(Path.Combine(root, "Web", "Components", "Shared", "DeploymentAnalysisPanel.razor.css"),
            Path.Combine(preview, "analysis.css"), true);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "app.css"), Path.Combine(preview, "app.css"), true);
        File.Copy(Path.Combine(root, "Web", "wwwroot", "lib", "bootstrap", "dist", "css", "bootstrap.min.css"),
            Path.Combine(preview, "bootstrap.css"), true);
    }

    /// <summary>Missing consent, operator disablement or nonfailed deployment disables manual requests with an explanation.</summary>
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

    /// <summary>A result for another deployment cannot appear under the current selection.</summary>
    [Fact]
    public async Task Stale_result_is_omitted()
    {
        var html = await RenderAsync(Result(AiAnalysisStatus.Completed), deploymentId: Guid.NewGuid());
        Assert.Contains("No analysis yet", html);
        Assert.DoesNotContain("order:42", html);
        Assert.DoesNotContain("Cancel analysis", html);
    }

    /// <summary>Cancellation remains available for active work when new requests are disabled or consent revoked.</summary>
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

    /// <summary>Quota, missing context and unavailable configuration use fixed guidance, including hostile legacy codes.</summary>
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

    /// <summary>Known synthetic safe view, with hostile HTML text to prove normal Razor encoding.</summary>
    private static DeploymentAnalysisView Result(AiAnalysisStatus status)
    {
        return new DeploymentAnalysisView(Guid.NewGuid(), Guid.NewGuid(), status,
            AiAnalysisTrigger.Manual, "<script>alert('example')</script>", ["Review configuration"], ["order:42"],
            status == AiAnalysisStatus.Skipped ? "unsupported_data" : "unavailable", DateTimeOffset.UtcNow,
            status is AiAnalysisStatus.Queued or AiAnalysisStatus.Running ? null : DateTimeOffset.UtcNow,
            "openai", "gpt-5-mini", PromptVersion: "deployment-diagnostics-v2", ResultSchemaVersion: 1);
    }

    /// <summary>Uses the real Razor rendering pipeline without backend or provider services.</summary>
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

    /// <summary>Locates preview output under the actual repository.</summary>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AutoMate.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }

    /// <summary>Records exact consent identities and supplies newly persisted configuration through the application port.</summary>
    public class ConsentPort : DispatchProxy
    {
        /// <summary>Configuration returned by authorized readback.</summary>
        public Domain.Entities.Application? Saved { get; set; }

        /// <summary>Exact application, owner, project and consent passed by the page.</summary>
        public (Guid App, Guid Owner, Guid Project, bool Consent) Target { get; private set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "SetAiDiagnosticEgressConsentAsync" && args?.Length == 5)
            {
                Target = ((Guid)args[0]!, (Guid)args[1]!, (Guid)args[2]!, (bool)args[3]!);
                return Task.FromResult(true);
            }

            if (method?.Name == "GetAppByIdAsync") return Task.FromResult(Saved);
            throw new NotSupportedException();
        }
    }

    /// <summary>Enabled UI options do not imply approved egress; the fake port never transmits anything.</summary>
    private sealed class EnabledOptions : IOptionsMonitor<AiAnalysisOptions>
    {
        /// <inheritdoc />
        public AiAnalysisOptions CurrentValue { get; } = new() { Enabled = true };

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

    /// <summary>Captures actual owner/deployment/stable-ID arguments and simulates one uncertain admission response.</summary>
    public class RequestPort : DispatchProxy
    {
        /// <summary>Stable identities observed by the Application port.</summary>
        public List<Guid> Requests { get; } = [];

        /// <summary>Owner metadata supplied by the page.</summary>
        public Guid Owner { get; private set; }

        /// <summary>Actual deployment metadata supplied by the page.</summary>
        public Guid Deployment { get; private set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "RequestManualAsync" || args![2] is not Guid id) throw new NotSupportedException();
            Owner = (Guid)args[0]!;
            Deployment = (Guid)args[1]!;
            Requests.Add(id);
            return Requests.Count == 1
                ? Task.FromException<DeploymentAnalysisRequestResult>(
                    new InvalidOperationException("private-provider-error"))
                : Task.FromResult(new DeploymentAnalysisRequestResult(true, "Analysis queued.",
                    Result(AiAnalysisStatus.Queued) with { DeploymentId = Deployment }));
        }
    }
}