using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Execution;
using Application.Ai.Context;
using Application.Data.Apps;
using Domain.Enums.Ai;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared;

/// <summary>
///     Coordinates owned analysis choices, consent, requests, cancellation and bounded result polling.
/// </summary>
public partial class DeploymentAnalysisSection
{
    /// <summary>Cancels reads/actions and the independent poll loop on disposal.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Serializes UI actions and poll reads.</summary>
    private bool _busy;

    /// <summary>Current owner-authorized consent state.</summary>
    private bool _consented;

    /// <summary>Current selection generation; rejects late action responses.</summary>
    private (Guid Owner, Guid Project, Guid? Deployment)? _identity;

    /// <summary>Latest selected deployment result.</summary>
    private DeploymentAnalysisView? _latest;

    /// <summary>Fixed customer-facing action feedback.</summary>
    private string? _message;

    /// <summary>Limits catalog polling independently of five-second result polling.</summary>
    private DateTimeOffset _nextCatalogAt;

    /// <summary>Saved result page offset.</summary>
    private int _offset;

    /// <summary>Independent saved-result poll task.</summary>
    private Task? _poll;

    /// <summary>Recorded context catalog for the selected deployment.</summary>
    private AssessmentPreferences? _preferences;

    /// <summary>Stable request identity retained only after uncertain admission.</summary>
    private Guid? _requestId;

    /// <summary>Bounded saved result page, plus one pagination sentinel.</summary>
    private IReadOnlyList<DeploymentAnalysisView> _runs = [];

    /// <summary>Stops work on route changes without stopping the whole component.</summary>
    private CancellationTokenSource? _selection;

    /// <summary>Editable selection; admission stores a separate immutable copy.</summary>
    private AssessmentSelection _selectionDraft = new();

    /// <summary>Immutable options retained with an uncertain pending request.</summary>
    private AssessmentSelection? _submitted;

    /// <summary>Resolved account identity; server ports recheck ownership.</summary>
    [Parameter]
    public Guid OwnerId { get; set; }

    /// <summary>Owning dashboard project.</summary>
    [Parameter]
    public Guid ProjectId { get; set; }

    /// <summary>Exact deployment shown on either current or historical page.</summary>
    [Parameter]
    public Guid? DeploymentId { get; set; }

    /// <summary>Exact C# project whose current egress consent controls analysis.</summary>
    [Parameter]
    public Guid CsProjectId { get; set; }

    /// <summary>Consent read by the parent for the initial selection.</summary>
    [Parameter]
    public bool Consented { get; set; }

    /// <summary>Prevents editing a submitted request or an unavailable deployment selection.</summary>
    private bool ControlsFrozen => OwnerId == Guid.Empty || DeploymentId is null || _busy || _requestId is not null ||
                                   _latest?.Status is AiAnalysisStatus.Queued or AiAnalysisStatus.Running;

    /// <summary>
    ///     Cancels component reads and polling and releases owned sources without canceling durable
    ///     server analyses.
    /// </summary>
    /// <returns>A task representing completion of component cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_selection is { } previousSelection) await previousSelection.CancelAsync();

        if (_poll is not null) await _poll;
        _selection?.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    ///     Updates only the editable draft while no request is pending.
    /// </summary>
    /// <param name="selection">Immutable assessment context choices.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task SelectionChanged(AssessmentSelection selection)
    {
        if (!ControlsFrozen) _selectionDraft = selection;
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Saves deployment preferences without rewriting earlier requested analyses.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task SavePreferencesAsync()
    {
        return ActAsync(async (service, token) =>
        {
            var identity = _identity;
            var selected = _selectionDraft.Normalize();
            var saved = await service.SavePreferencesAsync(OwnerId, DeploymentId!.Value, selected, token);
            if (identity == _identity && !token.IsCancellationRequested)
                _message = saved ? "Context choices saved for this deployment." : "Context choices could not be saved.";
        });
    }

    /// <summary>
    ///     Resets saved choices and results for changed owner/deployment parameters and starts bounded
    ///     result polling.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnParametersSetAsync()
    {
        var identity = (OwnerId, ProjectId, DeploymentId);

        if (_identity == identity)
            return;
        if (_selection is { } previousSelection) await previousSelection.CancelAsync();

        _selection?.Dispose();
        _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _identity = identity;
        _consented = Consented;
        _latest = null;
        _runs = [];
        _offset = 0;
        _requestId = null;
        _message = null;
        _preferences = null;
        _submitted = null;
        _selectionDraft = new AssessmentSelection();
        _nextCatalogAt = default;

        await RefreshAsync();
        _poll ??= PollAsync();
    }

    /// <summary>
    ///     Refreshes saved state every five seconds without overlapping owner actions.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                await InvokeAsync(async () =>
                {
                    await RefreshAsync();
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     Loads current and historical saved runs through a fresh service scope.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task RefreshAsync()
    {
        return ActAsync(async (service, token) =>
        {
            var identity = _identity;

            if (DeploymentId is not { } deployment)
                return;

            AssessmentPreferences? preferences = null;

            if (_preferences is null || DateTimeOffset.UtcNow >= _nextCatalogAt)
                preferences = await service.GetPreferencesAsync(OwnerId, deployment, token);

            var latest = await service.GetLatestAsync(OwnerId, deployment, token);
            var runs = await service.ListAsync(OwnerId, deployment, _offset, 21, token);

            if (identity != _identity || token.IsCancellationRequested)
                return;

            if (preferences is not null)
            {
                if (_preferences is null) _selectionDraft = preferences.Selection;
                _preferences = preferences;
                _nextCatalogAt = DateTimeOffset.UtcNow.AddMinutes(1);
            }

            _latest = latest;
            _runs = runs;
        });
    }

    /// <summary>
    ///     Requests a new manual assessment; only uncertain responses retain the idempotency key.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task RequestAsync()
    {
        return ActAsync(async (service, token) =>
        {
            if (DeploymentId is not { } deployment || !_consented || !Options.CurrentValue.Enabled)
                return;

            var identity = _identity;
            var owner = OwnerId;

            _submitted ??= _selectionDraft.Normalize();
            _requestId ??= Guid.NewGuid();

            var submitted = _submitted;
            var requestId = _requestId.Value;
            await service.SavePreferencesAsync(owner, deployment, submitted, token);

            if (identity != _identity || token.IsCancellationRequested)
                return;

            var result = await service.RequestManualAsync(owner, deployment, requestId, submitted, token);

            if (identity != _identity || token.IsCancellationRequested)
                return;

            _requestId = null;
            _submitted = null;
            _offset = 0;

            if (result.Analysis is not null)
                _latest = result.Analysis;

            _message = result.Message;
            var runs = await service.ListAsync(OwnerId, deployment, 0, 21, token);

            if (identity == _identity && !token.IsCancellationRequested)
                _runs = runs;
        });
    }

    /// <summary>
    ///     Cancels only the latest selected deployment's active analysis.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task CancelAsync()
    {
        return ActAsync(async (service, token) =>
        {
            if (DeploymentId is not { } deployment
                || _latest is not { } latest
                || latest.DeploymentId != deployment) return;

            var identity = _identity;
            var outcome = await service.CancelAsync(OwnerId, deployment, latest.Id, token);

            if (identity != _identity || token.IsCancellationRequested)
                return;

            var updated = await service.GetLatestAsync(OwnerId, deployment, token);

            if (identity == _identity && !token.IsCancellationRequested)
            {
                _latest = updated;
                _message = outcome switch
                {
                    DeploymentAnalysisCancellationResult.Cancelled => "Analysis canceled.",
                    DeploymentAnalysisCancellationResult.AlreadyFinished =>
                        "Analysis already finished; its saved result is preserved.",
                    _ => "Analysis is no longer available."
                };
            }
        });
    }

    /// <summary>
    ///     Edits current consent for the exact project; snapshots never grant consent.
    /// </summary>
    /// <param name="consented">Whether the owner permits diagnostic context to leave AutoMate.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task ConsentAsync(bool consented)
    {
        return ActAsync(async (_, token) =>
        {
            var identity = _identity;
            await using var scope = Scopes.CreateAsyncScope();
            var changed = await scope.ServiceProvider.GetRequiredService<IApplicationService>()
                .SetAiDiagnosticEgressConsentAsync(ProjectId, OwnerId, CsProjectId, consented, token);

            if (identity == _identity && !token.IsCancellationRequested)
            {
                if (changed)
                {
                    _consented = consented;
                    _message = consented
                        ? "Diagnostic data egress consent saved for this deployment's project."
                        : "Diagnostic data egress consent revoked. Use Cancel analysis to stop queued or running work.";
                }
                else
                {
                    _message = "Consent could not be updated.";
                }
            }
        });
    }

    /// <summary>
    ///     Moves one bounded result page.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task PreviousAsync()
    {
        if (_busy) return;
        _offset = Math.Max(0, _offset - 20);
        await RefreshAsync();
    }

    /// <summary>
    ///     Moves one bounded result page.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task NextAsync()
    {
        if (_busy) return;
        _offset += 20;
        await RefreshAsync();
    }

    /// <summary>
    ///     Isolates request scopes and prevents stale error feedback after navigation.
    /// </summary>
    /// <param name="action">Scoped analysis operation receiving its service and cancellation token.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task ActAsync(Func<IDeploymentAnalysisService, CancellationToken, Task> action)
    {
        if (_busy || _lifetime.IsCancellationRequested || OwnerId == Guid.Empty || DeploymentId is null) return;
        var identity = _identity;
        var token = _selection?.Token ?? _lifetime.Token;
        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisService>(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ArgumentException)
        {
            if (identity == _identity && !token.IsCancellationRequested)
            {
                _requestId = null;
                _submitted = null;
                _message =
                    "Choose valid context options and a UTC range of at most 365 days, ending no later than now.";
            }
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "Analysis action unavailable: {FailureType}.", error.GetType().Name);
            if (identity == _identity && !token.IsCancellationRequested)
                _message = "Analysis could not be updated. Refresh or retry shortly.";
        }
        finally
        {
            _busy = false;
        }
    }
}
