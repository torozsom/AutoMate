using Application.Abstractions.Diagnostics.Metrics;
using Application.Data.Apps;
using Application.Data.Users;
using Domain.Enums.Deployments;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Web.Components.Pages.Identity;

namespace Web.Components.Shared.Workspace;

/// <summary>
///     Loads owner-scoped overview data through independent scopes and visibility-aware polling.
/// </summary>
public partial class WorkspaceOverviewPanel
{
    /// <summary>Request and polling lifetime.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Controls repeated refresh requests.</summary>
    private bool _busy;

    /// <summary>Last successful authorized projection.</summary>
    private WorkspaceOverview? _data;

    /// <summary>Local visibility helpers.</summary>
    private IJSObjectReference? _module;

    /// <summary>Safe read failure guidance.</summary>
    private string? _notice;

    /// <summary>Internal owner identity resolved from authentication.</summary>
    private Guid _owner;

    /// <summary>Polling task observed at disposal.</summary>
    private Task? _polling;

    /// <summary>Custom windows stay fixed while relative presets advance on refresh.</summary>
    private string _preset = "30d";

    /// <summary>Selected absolute window, refreshed only for relative selections by the picker.</summary>
    private MetricTimeRange _range = MetricTimeRange.Preset("30d", DateTimeOffset.UtcNow);

    /// <summary>Cancellation of obsolete range reads.</summary>
    private CancellationTokenSource? _request;

    /// <summary>Invalidates stale replies across identity changes.</summary>
    private int _version;

    /// <summary>
    ///     Unsubscribes notifications, cancels polling and pending reads, and releases owned resources.
    /// </summary>
    /// <returns>A task representing completion of component cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        Authentication.AuthenticationStateChanged -= AuthenticationChanged;
        StatusNotifier.OnStatusChanged -= StatusChanged;
        await _lifetime.CancelAsync();

        if (_polling is not null)
            await _polling;

        if (_module is not null)
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }

        _lifetime.Dispose();
    }

    /// <summary>
    ///     Starts the new range immediately and cancels the older read.
    /// </summary>
    /// <param name="range">Validated UTC metric window selected by the user.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RangeChangedAsync(MetricTimeRange range)
    {
        if (_request is { } previousRequest) await previousRequest.CancelAsync();
        _busy = false;
        _range = range;
        await LoadAsync();
    }

    /// <summary>
    ///     Subscribes to deployment and identity notifications and loads the initial owned overview.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnInitializedAsync()
    {
        Authentication.AuthenticationStateChanged += AuthenticationChanged;
        StatusNotifier.OnStatusChanged += StatusChanged;
        using var scope = Scopes.CreateScope();
        _owner = await AuthenticatedUserResolver.GetCurrentUserIdAsync(Authentication,
            scope.ServiceProvider.GetRequiredService<IUserService>());
        await LoadAsync();
    }

    /// <summary>
    ///     Reads in an independent database scope and fences replies against disposal/window changes.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task LoadAsync()
    {
        if (_owner == Guid.Empty || _lifetime.IsCancellationRequested || _busy)
            return;
        _busy = true;

        if (_preset != "custom")
            _range = MetricTimeRange.Preset(_preset, DateTimeOffset.UtcNow);

        var range = _range;
        if (_request is { } previousRequest) await previousRequest.CancelAsync();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _request = request;
        var owner = _owner;
        var version = ++_version;

        try
        {
            using var scope = Scopes.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceQuery>()
                .OverviewAsync(owner, range, request.Token);
            if (!request.IsCancellationRequested && range == _range && owner == _owner && version == _version)
            {
                _data = result;
                _notice = null;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Logger.LogWarning("Workspace overview refresh failed: {FailureType}.", error.GetType().Name);
            if (!_lifetime.IsCancellationRequested && version == _version)
                _notice = "Workspace statistics could not be refreshed. Try again shortly.";
        }
        finally
        {
            if (version == _version)
            {
                _busy = false;
                _request = null;
            }
        }
    }

    /// <summary>
    ///     Starts visibility-aware overview polling after the first interactive render.
    /// </summary>
    /// <param name="firstRender">Whether this is the first interactive render.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/console-ui.js");
            if (_lifetime.IsCancellationRequested)
            {
                await module.DisposeAsync();
                return;
            }

            _module = module;
            _polling = PollAsync();
        }
        catch (JSException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _notice = "Automatic refresh is unavailable. Use Refresh to update the overview.";
                StateHasChanged();
            }
        }
    }

    /// <summary>
    ///     Refreshes every thirty seconds only while the browser page is visible.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                if (_module is not null && await _module.InvokeAsync<bool>("isVisible", _lifetime.Token))
                    await InvokeAsync(async () =>
                    {
                        await LoadAsync();
                        if (!_lifetime.IsCancellationRequested) StateHasChanged();
                    });
        }
        catch (OperationCanceledException)
        {
        }
        catch (JSException)
        {
            /* A disconnected browser ends automatic polling; manual reload reconnects it. */
        }
        catch (InvalidOperationException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     Refreshes saved workspace state following deployment notifications.
    /// </summary>
    /// <param name="project">Project identity associated with this operation.</param>
    /// <param name="status">New deployment status.</param>
    private void StatusChanged(Guid project, DeploymentStatus status)
    {
        _ = RefreshNotificationAsync(false);
    }

    /// <summary>
    ///     Clears the read model when the authenticated identity changes.
    /// </summary>
    /// <param name="state">Pending authentication state used to detect an owner change.</param>
    private void AuthenticationChanged(Task<AuthenticationState> state)
    {
        _ = RefreshNotificationAsync(true);
    }

    /// <summary>
    ///     Marshals background events and observes disposal without leaking stale owner data.
    /// </summary>
    /// <param name="identityChanged">Whether to discard data from the previous authenticated owner.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RefreshNotificationAsync(bool identityChanged)
    {
        if (_lifetime.IsCancellationRequested) return;
        try
        {
            await InvokeAsync(async () =>
            {
                if (_lifetime.IsCancellationRequested) return;
                if (identityChanged)
                {
                    _data = null;
                    _notice = null;
                    _owner = Guid.Empty;
                    ++_version;
                    _busy = false;
                    StateHasChanged();
                    using var scope = Scopes.CreateScope();
                    _owner = await AuthenticatedUserResolver.GetCurrentUserIdAsync(Authentication,
                        scope.ServiceProvider.GetRequiredService<IUserService>(), _lifetime.Token);
                }

                await LoadAsync();
                if (!_lifetime.IsCancellationRequested) StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Logger.LogWarning("Workspace notification refresh failed: {FailureType}.", error.GetType().Name);
        }
    }
}
