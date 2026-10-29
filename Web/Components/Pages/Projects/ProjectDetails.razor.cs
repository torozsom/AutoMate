using System.Globalization;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Orchestration.Cloud;
using Application.Orchestration.Jobs;
using Application.Orchestration.Status;
using Domain.DTO.Deployment;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR.Client;
using Web.Components.Pages.Identity;
using Web.Components.Shared.Configuration;
using Web.Components.Shared.Console;
using Web.Components.Shared.Projects;
using Web.Hubs;
using Web.Services;

namespace Web.Components.Pages.Projects;

/// <summary>
///     A Blazor component that displays the details of a specific project, including its name,
///     description, and other relevant information. This component retrieves the project details
///     based on the provided ProjectId parameter and ensures that the user is authenticated before
///     displaying the information.
/// </summary>
public partial class ProjectDetails : ComponentBase, IAsyncDisposable
{
    /// <summary>Stops database-backed status and terminal catch-up when the page closes.</summary>
    private readonly CancellationTokenSource _cloudPollCancellation = new();

    /// <summary>Prevents overlapping status refreshes in one Blazor circuit.</summary>
    private readonly SemaphoreSlim _cloudPollGate = new(1, 1);

    /// <summary>Latest compatibility CPU and memory display text by resource.</summary>
    private readonly Dictionary<string, (string Cpu, string Memory)> _containerMetrics = new();

    /// <summary>Initialized database terminals keyed by tab identity.</summary>
    private readonly Dictionary<string, Terminal> _dbTerminals = new();


    /// <summary>Recorded resource names available for metric-card navigation.</summary>
    private readonly List<string> _metricContainerNames = [];


    /// <summary>Owns bounded buffering and deduplication independently of terminal rendering.</summary>
    private readonly ProjectTerminalReplayState _terminalReplay = new();

    /// <summary>Serializes initial, reconnect and periodic replay handshakes.</summary>
    private readonly SemaphoreSlim _terminalReplayGate = new(1, 1);

    /// <summary>Native per-series observations fence out stale live and saved samples.</summary>
    private readonly Dictionary<string, Dictionary<string, DeploymentMetricPoint>> _typedMetrics = new();

    /// <summary>Selected terminal channel key.</summary>
    private string _activeTab = "build";

    /// <summary>Prevents late page reads from publishing after disposal.</summary>
    private bool _analysisDisposed;

    /// <summary>Current owned project, or null before loading.</summary>
    private Domain.Entities.Projects.Application? _app;

    /// <summary>Displays redacted managed Azure database resource logs.</summary>
    private Terminal? _azureDatabaseTerminal;

    /// <summary>Initialized azure system terminal component, or null before rendering.</summary>
    private Terminal? _azureSystemTerminal;

    /// <summary>Initialized azure web terminal component, or null before rendering.</summary>
    private Terminal? _azureWebTerminal;

    /// <summary>Project bound to this component's current subscription.</summary>
    private Guid _boundProjectId;

    /// <summary>Initialized build terminal component, or null before rendering.</summary>
    private Terminal? _buildTerminal;

    /// <summary>Background refresh task for multi-instance SaaS deployments.</summary>
    private Task? _cloudPollTask;

    /// <summary>Newest durable SaaS run displayed by this page.</summary>
    private CloudDeploymentReceipt? _cloudRunReceipt;

    /// <summary>User-editable deployment configuration for the open form.</summary>
    private DeploymentConfigDto? _currentDeployConfig;

    /// <summary>Selected resource position in the metric-card carousel.</summary>
    private int _currentMetricIndex;

    /// <summary>Resolved internal owner identity.</summary>
    private Guid _currentUserId;

    /// <summary>Database channels discovered from project configuration.</summary>
    private IEnumerable<DatabaseTab> _databaseTabs = [];


    /// <summary>Bounded six-item deployment history page.</summary>
    private int _deploymentPage;

    /// <summary>Initialized github actions terminal component, or null before rendering.</summary>
    private Terminal? _githubActionsTerminal;

    /// <summary>Owned SignalR connection for project diagnostics.</summary>
    private HubConnection? _hubConnection;

    /// <summary>Prevents duplicate SignalR initialization during rendering.</summary>
    private bool _hubInitializationStarted;


    /// <summary>Whether deployment admission is in progress.</summary>
    private bool _isDeploying;

    /// <summary>Whether initial owned data is being loaded.</summary>
    private bool _isLoading = true;

    /// <summary>Whether a stop request is in progress.</summary>
    private bool _isStopping;

    /// <summary>Suppresses unchanged storage-status notices during periodic catch-up.</summary>
    private string? _lastHistoryAvailability;


    /// <summary>Bounds saved metric recovery while waiting for the first live sample.</summary>
    private DateTimeOffset _nextMetricReplayAt;

    /// <summary>Rejects terminal replies overtaken by navigation or a deployment replacement.</summary>
    private long _pageGeneration;

    /// <summary>Stable key used if the current SaaS admission request is retried.</summary>
    private string? _pendingCloudIdempotencyKey;

    /// <summary>Fixed recoverable metadata-read guidance; provider details never reach markup.</summary>
    private string? _projectReadError;

    /// <summary>Independent server-side totals and the selected bounded history page.</summary>
    private ProjectDetailsReadModel? _projectView;

    /// <summary>Renderer-owned refresh admission and pending retry state.</summary>
    private bool _refreshInProgress;

    /// <summary>Coalesces notifications arriving while a metadata read is awaited.</summary>
    private bool _refreshPending;

    /// <summary>Rejoins the authorized stream after the replacement route has rendered terminals.</summary>
    private bool _rejoinPending;

    /// <summary>Preserves startup port resolution requested by a coalesced notification.</summary>
    private bool _resolvePortPending;

    /// <summary>Local project directory supplied to variable discovery.</summary>
    private string? _selectedProjectPath;

    /// <summary>Shows the deployment-scoped assessment dialog.</summary>
    private bool _showAnalysisDialog;

    /// <summary>Whether deployment settings are visible.</summary>
    private bool _showConfigModal;

    /// <summary>Distinguishes recovered observations from fresh live samples.</summary>
    private bool _showSavedMetricNotice;


    /// <summary>Deployment identity to which current terminal state belongs.</summary>
    private Guid? _terminalDeploymentId;

    /// <summary>Whether active deployment history still needs restoring.</summary>
    private bool _terminalReplayPending = true;

    /// <summary>Discovered local web port; zero means unavailable.</summary>
    private int _webHostPort;

    /// <summary>Initialized web terminal component, or null before rendering.</summary>
    private Terminal? _webTerminal;

    /// <summary>Safe workflow admission or progress guidance.</summary>
    private string? _workflowStatusMessage;

    /// <summary>Route identity of the owner-scoped project.</summary>
    [Parameter]
    public Guid ProjectId { get; set; }


    /// <summary>Current circuit authentication state.</summary>
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// <summary>Admits bounded deployment jobs.</summary>
    [Inject]
    private IDeploymentJobQueue DeploymentJobQueue { get; set; } = null!;


    /// <summary>Prepares cloud connections and admits requests without exposing provider credentials.</summary>
    [Inject]
    private ConnectedCloudDeploymentAdmission CloudAdmission { get; set; } = null!;

    /// <summary>Creates an independent read scope for each cross-instance status poll.</summary>
    [Inject]
    private IServiceScopeFactory ScopeFactory { get; set; } = null!;


    /// <summary>Application navigation and absolute-link generation.</summary>
    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    /// <summary>Protects short-lived project log subscription tokens.</summary>
    [Inject]
    private IDataProtectionProvider DataProtectionProvider { get; set; } = null!;

    /// <summary>Discovers deployment metadata and configuration variables.</summary>
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

    /// <summary>Subscribes to project deployment status changes.</summary>
    [Inject]
    private IDeploymentStatusNotifier DeploymentStatusNotifier { get; set; } = null!;

    /// <summary>Local Docker operations permitted by the hosting profile.</summary>
    [Inject]
    private IDockerService DockerService { get; set; } = null!;

    /// <summary>Server-enforced hosting-profile capability flags.</summary>
    [Inject]
    private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>Reviewed operational logger for the component.</summary>
    [Inject]
    private ILogger<ProjectDetails> Logger { get; set; } = null!;

    /// <summary>
    ///     Disposes of the component by leaving the SignalR group associated with the project and
    ///     disposing of the hub connection.
    /// </summary>
    /// <returns>A task representing completion of component cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        _analysisDisposed = true;
        _pageGeneration++;
        await _cloudPollCancellation.CancelAsync();
        if (_cloudPollTask is not null)
            try
            {
                await _cloudPollTask;
            }
            catch (OperationCanceledException)
            {
            }

        _cloudPollGate.Dispose();
        DeploymentStatusNotifier.OnStatusChanged -= OnDeploymentStatusChanged;
        DeploymentJobQueue.StateChanged -= OnQueueStateChanged;

        if (_hubConnection is not null)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
                await _hubConnection.SendAsync("LeaveProjectGroup", ProjectId);

            await _hubConnection.DisposeAsync();
        }

        _cloudPollCancellation.Dispose();
        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     Determines whether a deployment is currently in progress.
    /// </summary>
    /// <returns>True if a deployment is in progress, otherwise false.</returns>
    private bool IsDeploying()
    {
        var status = GetLatestStatus();
        var queueState = DeploymentJobQueue.GetProjectState(ProjectId);
        return _isDeploying || status == DeploymentStatus.Starting ||
               _cloudRunReceipt?.Phase is CloudRunPhase.Queued or CloudRunPhase.Preparing or
                   CloudRunPhase.AwaitingWorkflow or CloudRunPhase.WorkflowRunning ||
               queueState.QueuedDeployments > 0 || queueState.ActiveDeployments > 0;
    }

    /// <summary>
    ///     Schedules a renderer update when this project queue changes.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    private void OnQueueStateChanged(Guid projectId)
    {
        _ = DispatchNotificationAsync(() =>
        {
            if (projectId == ProjectId) StateHasChanged();
            return Task.CompletedTask;
        });
    }

    /// <summary>Observes renderer callback faults and fences notifications after navigation.</summary>
    private async Task DispatchNotificationAsync(Func<Task> notification)
    {
        if (_analysisDisposed) return;
        try
        {
            await InvokeAsync(async () =>
            {
                if (!_analysisDisposed) await notification();
            });
        }
        catch (Exception) when (_analysisDisposed)
        {
            // A queued callback may outlive the renderer during navigation.
        }
        catch (Exception error)
        {
            Logger.LogWarning("Project notification failed for {ProjectId}: {FailureType}.",
                ProjectId, error.GetType().Name);
        }
    }


    /// <summary>
    ///     Initiates the deployment process for the project by analyzing its dependencies and preparing
    ///     the deployment configuration.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task DeployProjectAsync()
    {
        if (_app == null) return;

        if (_app.SourceType == SourceType.Local && !DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            _workflowStatusMessage =
                "Local Docker deployments are available only in a self-hosted AutoMate installation.";
            return;
        }

        if (_app.SourceType == SourceType.Remote)
        {
            _selectedProjectPath = string.Empty;
            _currentDeployConfig = CreateCloudDeploymentConfig(_app);
            _showConfigModal = true;
            return;
        }

        var csProject = _app.CsProjects.FirstOrDefault(p => p.IsWebProject);
        if (csProject == null) return;

        _selectedProjectPath = csProject.Path;
        _currentDeployConfig = await ProjectScanner.AnalyzeDependenciesAsync(_app, csProject);

        _showConfigModal = true;
    }


    /// <summary>
    ///     Stops the current deployment asynchronously.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task StopDeploymentAsync()
    {
        if (_app == null) return;
        if (_app.SourceType != SourceType.Local)
        {
            _workflowStatusMessage = "Stop cloud deployments in your cloud provider's portal.";
            return;
        }

        if (!DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            _workflowStatusMessage = "Local Docker deployments are disabled for this AutoMate instance.";
            return;
        }

        var csProject = _app.CsProjects.FirstOrDefault(p => p.IsWebProject);
        if (csProject == null) return;

        _isStopping = true;
        StateHasChanged();

        try
        {
            await DeploymentJobQueue.EnqueueAsync(new StopLocalDeploymentJob(ProjectId, _app.Name, csProject.Path));
            _isStopping = false;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to queue stop deployment for project {ProjectId} Failure {FailureType}.", ProjectId,
                ex.GetType().Name);
            _isStopping = false;
            StateHasChanged();
        }
    }


    /// <summary>
    ///     Hides the deployment configuration modal and resets the related state variables to their
    ///     default values.
    /// </summary>
    private void HideConfigModal()
    {
        _showConfigModal = false;
        _currentDeployConfig = null;
        _selectedProjectPath = null;
    }


    /// <summary>
    ///     Renders the deployment configuration modal when enough state is available.
    /// </summary>
    /// <returns>A fragment rendering the current configuration form, or no content until its inputs are ready.</returns>
    private RenderFragment RenderConfigurationForm()
    {
        return builder =>
        {
            if (!_showConfigModal || _currentDeployConfig is null || _selectedProjectPath is null)
                return;

            builder.OpenComponent<ConfigurationForm>(0);

            builder.AddAttribute(1, nameof(ConfigurationForm.Config), _currentDeployConfig);
            builder.AddAttribute(2, nameof(ConfigurationForm.ProjectPath), _selectedProjectPath);
            builder.AddAttribute(3, nameof(ConfigurationForm.IsCloudDeployment),
                _currentDeployConfig.IsCloudDeployment);
            builder.AddAttribute(4, nameof(ConfigurationForm.OnCancel),
                EventCallback.Factory.Create(this, HideConfigModal));
            builder.AddAttribute(5, nameof(ConfigurationForm.OnDeployConfirmed),
                EventCallback.Factory.Create<DeploymentConfigDto>(this, ExecuteDeploymentAsync));

            builder.CloseComponent();
        };
    }


    /// <summary>
    ///     Executes the deployment process asynchronously by hiding the configuration modal, setting the
    ///     deploying state, and invoking the deployment orchestrator to deploy the project with the
    ///     specified configuration.
    /// </summary>
    /// <param name="finalConfig">The deployment configuration to be used for the project deployment.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task ExecuteDeploymentAsync(DeploymentConfigDto finalConfig)
    {
        HideConfigModal();
        _isDeploying = true;
        _webHostPort = 0;
        _workflowStatusMessage = null;
        StateHasChanged();

        if (finalConfig.IsCloudDeployment)
        {
            await ExecuteCloudDeploymentAsync(finalConfig);
            return;
        }

        try
        {
            await DeploymentJobQueue.EnqueueAsync(new LocalDeploymentJob(finalConfig));
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to queue deployment for project {ProjectId} Failure {FailureType}.",
                finalConfig.ProjectId, ex.GetType().Name);
            _workflowStatusMessage = ex is InvalidOperationException &&
                                     ex.Message == "The deployment queue is full. Try again after a job starts."
                ? "The deployment queue is full. Try again after a job starts."
                : "AutoMate could not queue this deployment. Try again shortly.";
            _isDeploying = false;
            StateHasChanged();
        }
    }

    /// <summary>
    ///     Queues the confirmed remote deployment through the hosting-profile admission path and reports
    ///     safe failures.
    /// </summary>
    /// <param name="finalConfig">Confirmed deployment configuration, including secret values that must not enter logs.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task ExecuteCloudDeploymentAsync(DeploymentConfigDto finalConfig)
    {
        if (_app == null)
            return;

        if (!GitHubRepositoryUrlParser.TryParse(_app.SourcePathOrUrl, out var repository))
        {
            _workflowStatusMessage = "AutoMate could not determine the GitHub repository owner/name.";
            _isDeploying = false;
            return;
        }

        var preparation = await CloudAdmission.PrepareAsync(_currentUserId, AuthStateProvider,
            false, () => true);

        if (preparation.State != CloudConnectionState.Ready)
        {
            _workflowStatusMessage = "Connect both GitHub and Azure before deploying to the cloud.";
            _isDeploying = false;
            return;
        }

        _workflowStatusMessage = "Configuring Azure OIDC and starting GitHub Actions workflow...";
        StateHasChanged();

        try
        {
            if (!DeploymentCapabilities.LocalDeploymentsEnabled)
            {
                _pendingCloudIdempotencyKey ??= Guid.NewGuid().ToString("N");
                _cloudRunReceipt = await preparation.StartAsync(finalConfig, repository.Owner,
                    repository.Name, _app.Name, _pendingCloudIdempotencyKey);
                _pendingCloudIdempotencyKey = null;
                UpdateCloudRunMessage();
            }
            else
            {
                await preparation.StartAsync(finalConfig, repository.Owner, repository.Name, _app.Name, null);
            }

            _workflowStatusMessage = "GitHub Actions workflow has been queued.";
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to queue cloud deployment for project {ProjectId} Failure {FailureType}.",
                finalConfig.ProjectId, ex.GetType().Name);
            _workflowStatusMessage = "Cloud deployment failed to queue. Verify provider access and try again shortly.";
            _isDeploying = false;
            StateHasChanged();
        }
    }


    /// <summary>
    ///     Asynchronously initializes the component by retrieving the current user's ID and fetching the
    ///     project details based on the provided ProjectId. If the project is found, it analyzes the
    ///     project's dependencies to determine the databases used and prepares the database tabs for
    ///     display.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnInitializedAsync()
    {
        DeploymentStatusNotifier.OnStatusChanged += OnDeploymentStatusChanged;
        DeploymentJobQueue.StateChanged += OnQueueStateChanged;

        _boundProjectId = ProjectId;
        _currentUserId = await GetCurrentUserIdAsync();
        await LoadCurrentProjectAsync();

        if (_app is not null)
            _cloudPollTask = PollTerminalHistoryAsync(_cloudPollCancellation.Token);
    }

    /// <summary>
    ///     Leaves the old project subscription and resets dialogs and replay state before loading a
    ///     changed route.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnParametersSetAsync()
    {
        if (_boundProjectId == Guid.Empty)
        {
            _boundProjectId = ProjectId;
            return;
        }

        if (_boundProjectId == ProjectId || _analysisDisposed)
            return;

        var previousProject = _boundProjectId;
        _boundProjectId = ProjectId;
        _pageGeneration++;
        _showAnalysisDialog = false;

        HideConfigModal();
        _app = null;
        _isLoading = true;
        _terminalDeploymentId = null;

        ResetTerminalState();

        if (_hubConnection?.State == HubConnectionState.Connected)
            await _hubConnection.SendAsync("LeaveProjectGroup", previousProject, _cloudPollCancellation.Token);

        await LoadCurrentProjectAsync();
        _rejoinPending = true;
    }

    /// <summary>
    ///     Loads route-specific data while fencing old route responses at every I/O boundary.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task LoadCurrentProjectAsync()
    {
        var project = ProjectId;
        var generation = _pageGeneration;
        try
        {
            _projectReadError = null;
            await LoadCurrentProjectCoreAsync();
        }
        catch (OperationCanceledException) when (_analysisDisposed || _cloudPollCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!_analysisDisposed && project == ProjectId && generation == _pageGeneration)
                _projectReadError = "Project details could not be loaded. Try again.";
            Logger.LogWarning("Project loading failed for {ProjectId}: {FailureType}.", project, error.GetType().Name);
        }
        finally
        {
            if (!_analysisDisposed && project == ProjectId && generation == _pageGeneration) _isLoading = false;
        }
    }

    /// <summary>Retries metadata without replacing the circuit or losing existing terminal output.</summary>
    private async Task RetryProjectReadAsync()
    {
        if (_app is null)
        {
            _isLoading = true;
            await LoadCurrentProjectAsync();
            if (_app is not null && _cloudPollTask is null)
                _cloudPollTask = PollTerminalHistoryAsync(_cloudPollCancellation.Token);
        }
        else
        {
            await RefreshProjectAsync();
        }
    }

    /// <summary>Loads detached route metadata with an operation-owned database scope.</summary>
    private async Task LoadCurrentProjectCoreAsync()
    {
        var project = ProjectId;
        var generation = _pageGeneration;
        var token = _cloudPollCancellation.Token;

        await using var scope = ScopeFactory.CreateAsyncScope();

        var view = _currentUserId == Guid.Empty
            ? null
            : await ReadProjectViewAsync(
                scope.ServiceProvider, project, _currentUserId, token);

        var app = view?.Project;
        if (_analysisDisposed || token.IsCancellationRequested || project != ProjectId ||
            generation != _pageGeneration) return;

        _projectView = view;
        _app = app;
        _terminalDeploymentId = GetLatestDeployment()?.Id;
        _activeTab = app?.SourceType == SourceType.Remote ? "github-actions" : "build";
        _databaseTabs = [];
        _cloudRunReceipt = null;
        _workflowStatusMessage = null;

        if (app is { SourceType: SourceType.Remote } && !DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            var receipt = await scope.ServiceProvider.GetRequiredService<ICloudDeploymentRunService>()
                .GetLatestForProjectAsync(_currentUserId, project, token);
            if (project != ProjectId || generation != _pageGeneration || token.IsCancellationRequested) return;
            _cloudRunReceipt = receipt;
            UpdateCloudRunMessage();
        }

        if (app is { SourceType: SourceType.Local } && app.CsProjects.FirstOrDefault(csp => csp.IsWebProject) is
                { } csProject)
        {
            var config = await ProjectScanner.AnalyzeDependenciesAsync(app, csProject, token);
            if (project != ProjectId || generation != _pageGeneration || token.IsCancellationRequested) return;
            _databaseTabs =
            [
                .. config.Databases.Select(db => new DatabaseTab(db.ContainerNameSuffix, db.DbType))
            ];
        }

        await UpdateWebHostPortAsync();
        if (project == ProjectId && generation == _pageGeneration && !token.IsCancellationRequested)
            _isLoading = false;
    }

    /// <summary>
    ///     Uses the bounded production query while retaining legacy host composition compatibility.
    /// </summary>
    /// <param name="services">The existing operation scope.</param>
    /// <param name="project">The captured project identity.</param>
    /// <param name="owner">The captured owner identity.</param>
    /// <param name="token">Cancels the current database reads.</param>
    /// <returns>The detached bounded view, or a legacy-compatible projection when the new port is absent.</returns>
    private async Task<ProjectDetailsReadModel?> ReadProjectViewAsync(IServiceProvider services, Guid project,
        Guid owner, CancellationToken token)
    {
        if (services.GetService<IProjectDetailsQuery>() is { } query)
            return await query.ReadAsync(project, owner, _deploymentPage, token);

        var app = await services.GetRequiredService<IApplicationService>().GetAppByIdAsync(project, owner, token);

        if (app is null) return null;

        var history = app.CsProjects
            .SelectMany(p => p.Deployments)
            .OrderBy(d => d.CreatedAt)
            .ToArray();

        return new ProjectDetailsReadModel(app, new ProjectDeploymentSummary(
                history.Length,
                history.Count(d => d.Outcome == DeploymentOutcome.Succeeded),
                history.Count(d => d.Outcome == DeploymentOutcome.Failed),
                history.Count(d => d.Outcome == DeploymentOutcome.Unknown),
                history.FirstOrDefault()?.CreatedAt),
            [
                .. history
                    .OrderByDescending(d => d.CreatedAt)
                    .ThenByDescending(d => d.Id)
                    .Skip(_deploymentPage * 6).Take(6)
            ]);
    }

    /// <summary>
    ///     Loads another history page while preserving the current renderer state and existing page numbering.
    /// </summary>
    /// <param name="delta">The existing newer/older page direction.</param>
    /// <returns>Completes after the selected owned page is refreshed.</returns>
    private async Task ChangeDeploymentPageAsync(int delta)
    {
        _deploymentPage += delta;
        await RefreshProjectAsync();
    }

    /// <summary>
    ///     Refreshes the current route through independent scopes and confirms active terminal replay
    ///     progress.
    /// </summary>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task PollTerminalHistoryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!await _cloudPollGate.WaitAsync(0, cancellationToken)) continue;
            try
            {
                await InvokeAsync(async () =>
                {
                    if (_analysisDisposed || _isLoading) return;
                    await RefreshProjectAsync();

                    if (_app?.SourceType == SourceType.Remote && !DeploymentCapabilities.LocalDeploymentsEnabled)
                    {
                        var project = ProjectId;
                        var generation = _pageGeneration;
                        await using var scope = ScopeFactory.CreateAsyncScope();
                        var receipt = await scope.ServiceProvider.GetRequiredService<ICloudDeploymentRunService>()
                            .GetLatestForProjectAsync(_currentUserId, project, cancellationToken);

                        if (project != ProjectId || generation != _pageGeneration ||
                            cancellationToken.IsCancellationRequested) return;

                        _cloudRunReceipt = receipt;
                        UpdateCloudRunMessage();
                    }

                    if (_hubConnection?.State == HubConnectionState.Connected) await JoinLogHubGroupAsync();
                    StateHasChanged();
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Project status/terminal refresh unavailable: {FailureType}.", ex.GetType().Name);
            }
            finally
            {
                _cloudPollGate.Release();
            }
        }
    }

    /// <summary>
    ///     Shows durable queue age and workflow phase after reloads and cross-instance updates.
    /// </summary>
    private void UpdateCloudRunMessage()
    {
        _workflowStatusMessage = _cloudRunReceipt?.Phase switch
        {
            CloudRunPhase.Queued =>
                $"Queued for {Math.Max(0, (int)(DateTimeOffset.UtcNow - _cloudRunReceipt.QueuedAt).TotalSeconds)} seconds.",
            CloudRunPhase.Preparing => "Configuring Azure and GitHub for deployment...",
            CloudRunPhase.AwaitingWorkflow => "GitHub Actions workflow is queued.",
            CloudRunPhase.WorkflowRunning => "GitHub Actions workflow is running.",
            CloudRunPhase.Succeeded => "GitHub Actions workflow completed successfully.",
            CloudRunPhase.Failed => _cloudRunReceipt.FailureReason ?? "Cloud deployment failed.",
            CloudRunPhase.TimedOut => _cloudRunReceipt.FailureReason ?? "Cloud workflow monitoring timed out.",
            _ => _workflowStatusMessage
        };
    }


    /// <summary>
    ///     Event handler that is called when the deployment status changes. It checks if the status
    ///     change is related to the current project, and if so, it updates the latest deployment status
    ///     in the project details and triggers a UI refresh.
    /// </summary>
    /// <param name="projectId">The ID of the project for which the status has changed.</param>
    /// <param name="status">The new deployment status.</param>
    private void OnDeploymentStatusChanged(Guid projectId, DeploymentStatus status)
    {
        _ = DispatchNotificationAsync(() => HandleDeploymentStatusChangedAsync(projectId, status));
    }


    /// <summary>
    ///     Handles the deployment status change by checking if the status change is relevant to the
    ///     current project, updating the latest deployment status, and refreshing the project details if
    ///     necessary.
    /// </summary>
    /// <param name="projectId">The ID of the project for which the status has changed.</param>
    /// <param name="status">The new deployment status.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task HandleDeploymentStatusChangedAsync(Guid projectId, DeploymentStatus status)
    {
        if (_app != null && _app.Id == projectId)
        {
            if (status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Stopped)
                _isDeploying = false;
            if (status == DeploymentStatus.Stopped)
                _isStopping = false;
            await RefreshProjectAsync(status == DeploymentStatus.Running);
        }
    }


    /// <summary>
    ///     Updates the exposed port for the web container by checking the latest deployment status and
    ///     retrieving the host port mapped to the web container from the Docker service if the deployment
    ///     is running.
    /// </summary>
    /// <param name="maxAttempts">Maximum port-discovery attempts.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task UpdateWebHostPortAsync(int maxAttempts = 1)
    {
        var project = ProjectId;
        var generation = _pageGeneration;
        if (_app is not { SourceType: SourceType.Local } || GetLatestStatus() != DeploymentStatus.Running)
        {
            _webHostPort = 0;
            return;
        }

        var csProject = _app.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
        if (csProject == null)
        {
            _webHostPort = 0;
            return;
        }

        var containerNames = GetWebContainerNameCandidates(csProject.Name);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            foreach (var containerName in containerNames)
            {
                var hostPort = await DockerService.GetContainerHostPortAsync(containerName);

                if (_analysisDisposed || project != ProjectId || generation != _pageGeneration)
                    return;

                if (hostPort <= 0) continue;

                _webHostPort = hostPort;
                return;
            }

            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(1));
        }

        _webHostPort = 0;
    }


    /// <summary>
    ///     Refreshes the project details by re-fetching the project from the database.
    /// </summary>
    /// <param name="resolveWebPortWithRetry">Whether to retry Docker port discovery while startup is in progress.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RefreshProjectAsync(bool resolveWebPortWithRetry = false)
    {
        _resolvePortPending |= resolveWebPortWithRetry;
        _refreshPending = true;

        if (_refreshInProgress || _analysisDisposed || _isLoading)
            return;

        _refreshInProgress = true;

        try
        {
            while (_refreshPending && !_analysisDisposed)
            {
                _refreshPending = false;
                var resolvePort = _resolvePortPending;
                _resolvePortPending = false;
                var project = ProjectId;
                var generation = _pageGeneration;

                try
                {
                    await RefreshProjectCoreAsync(resolvePort);
                    if (project == ProjectId) _projectReadError = null;
                }
                catch (OperationCanceledException) when (_analysisDisposed ||
                                                         _cloudPollCancellation.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    if (!_analysisDisposed && project == ProjectId && generation == _pageGeneration)
                        _projectReadError = "Project details could not be refreshed. Try again.";
                    Logger.LogWarning("Project refresh failed for {ProjectId}: {FailureType}.",
                        project, error.GetType().Name);
                }
            }
        }
        finally
        {
            _refreshInProgress = false;
            if (!_analysisDisposed) StateHasChanged();
        }
    }

    /// <summary>Applies one fenced refresh; the caller serializes refreshes across await boundaries.</summary>
    private async Task RefreshProjectCoreAsync(bool resolveWebPortWithRetry)
    {
        if (_currentUserId == Guid.Empty || _analysisDisposed || _isLoading) return;
        var project = ProjectId;
        var generation = _pageGeneration;
        await using var scope = ScopeFactory.CreateAsyncScope();

        var view = await ReadProjectViewAsync(scope.ServiceProvider, project, _currentUserId,
            _cloudPollCancellation.Token);

        var app = view?.Project;

        if (_analysisDisposed || generation != _pageGeneration || project != ProjectId)
            return;

        var previouslyActive = CanReplayTerminalHistory();
        _projectView = view;
        _app = app;
        var latestDeploymentId = GetLatestDeployment()?.Id;

        if (latestDeploymentId != _terminalDeploymentId)
        {
            _terminalDeploymentId = latestDeploymentId;
            _showAnalysisDialog = false;
            _pageGeneration++;
            ResetTerminalState();
            await ClearTerminalsAsync();
            if (_hubConnection?.State == HubConnectionState.Connected) await JoinLogHubGroupAsync();
        }
        else if (previouslyActive != CanReplayTerminalHistory())
        {
            // Keep visible output on stop, but invalidate replies issued before the status transition.
            _pageGeneration++;
            _terminalReplay.ClearPending();
            _terminalReplayPending = CanReplayTerminalHistory();
        }

        await UpdateWebHostPortAsync(resolveWebPortWithRetry ? 6 : 1);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    ///     Resets replay and metric state for a replacement deployment or route.
    /// </summary>
    private void ResetTerminalState()
    {
        _containerMetrics.Clear();
        _typedMetrics.Clear();
        _metricContainerNames.Clear();

        _currentMetricIndex = 0;
        _nextMetricReplayAt = DateTimeOffset.MinValue;
        _showSavedMetricNotice = false;
        _terminalReplay.Reset();

        _lastHistoryAvailability = null;
        _terminalReplay.ClearPending();
        _terminalReplay.AcknowledgeOverflow();
        _terminalReplayPending = true;
    }

    /// <summary>
    ///     Opens context configuration and saved results for the current deployment.
    /// </summary>
    private void OpenAnalysisDialog()
    {
        if (!_analysisDisposed && GetLatestDeployment() is not null) _showAnalysisDialog = true;
    }

    /// <summary>
    ///     Closes presentation without canceling durable analysis work.
    /// </summary>
    private void CloseAnalysisDialog()
    {
        _showAnalysisDialog = false;
    }

    /// <summary>
    ///     Restores the latest deployment's saved output even after it stops or fails.
    /// </summary>
    /// <returns>True when the project has a recorded deployment to replay.</returns>
    private bool CanReplayTerminalHistory()
    {
        return GetLatestDeployment() is not null;
    }

    /// <summary>
    ///     Rejects replies after disposal, navigation or deployment replacement.
    /// </summary>
    /// <param name="project">Project identity associated with this operation.</param>
    /// <param name="deployment">Deployment identity or metadata to inspect, if available.</param>
    /// <param name="generation">Page generation captured before the asynchronous read.</param>
    /// <returns>True only when the component, page generation, project and selected deployment still match.</returns>
    private bool IsCurrentTerminalRead(Guid project, Guid? deployment, long generation)
    {
        return !_analysisDisposed && generation == _pageGeneration && project == ProjectId &&
               deployment == _terminalDeploymentId && CanReplayTerminalHistory();
    }

    /// <summary>
    ///     Retrieves the latest deployment status for the project by looking at the most recent
    ///     deployment across all C# projects.
    /// </summary>
    /// <returns></returns>
    private DeploymentStatus? GetLatestStatus()
    {
        return _app?.CsProjects
            .SelectMany(c => c.Deployments)
            .MaxBy(d => d.CreatedAt)?.Status;
    }


    /// <summary>
    ///     Creates a cloud deployment configuration for a saved remote repository.
    /// </summary>
    /// <param name="app">Saved project metadata used for this deployment.</param>
    /// <returns>Default deployment settings for the saved remote repository.</returns>
    private static DeploymentConfigDto CreateCloudDeploymentConfig(Domain.Entities.Projects.Application app)
    {
        return CloudDeploymentPageDefaults.CreateConfiguration(app);
    }


    /// <summary>
    ///     Mirrors the Docker Compose template's web container name convention.
    /// </summary>
    /// <param name="csProjectName">Recorded C# web-project name.</param>
    /// <returns>Distinct web-container name candidates matching the Compose naming convention.</returns>
    private static IReadOnlyList<string> GetWebContainerNameCandidates(string csProjectName)
    {
        var normalizedName = $"{NormalizeContainerName(csProjectName)}-web";
        var legacyName = $"{csProjectName.Trim().ToLowerInvariant()}-web";

        return string.Equals(normalizedName, legacyName, StringComparison.OrdinalIgnoreCase)
            ? [normalizedName]
            : [normalizedName, legacyName];
    }


    /// <summary>
    ///     Normalizes a string to be used as a Docker container name by converting it to lowercase,
    ///     replacing non-alphanumeric characters with hyphens, and trimming leading/trailing hyphens.
    /// </summary>
    /// <param name="value">The string to normalize.</param>
    /// <returns>The normalized Docker container name.</returns>
    private static string NormalizeContainerName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "automate-project" : normalized;
    }


    /// <summary>
    ///     Selects the visible terminal channel.
    /// </summary>
    /// <param name="tabId">Stable terminal tab identity.</param>
    private void SetActiveTab(string tabId)
    {
        _activeTab = tabId;
    }


    /// <summary>
    ///     Chooses the terminal tab class for the current selection.
    /// </summary>
    /// <param name="tabId">Stable terminal tab identity.</param>
    /// <returns>The CSS classes for the selected state.</returns>
    private string GetTabClass(string tabId)
    {
        return _activeTab == tabId
            ? "terminal-tab-link active"
            : "terminal-tab-link";
    }


    /// <summary>
    ///     Builds the build-tab navigation class.
    /// </summary>
    /// <returns>The CSS classes for the selected state.</returns>
    private string GetBuildTabClass()
    {
        return $"nav-link {GetTabClass("build")}";
    }


    /// <summary>
    ///     Builds the web-tab navigation class.
    /// </summary>
    /// <returns>The CSS classes for the selected state.</returns>
    private string GetWebTabClass()
    {
        return $"nav-link {GetTabClass("web")}";
    }


    /// <summary>
    ///     Shows the selected terminal while retaining hidden terminal instances.
    /// </summary>
    /// <param name="tabId">Stable terminal tab identity.</param>
    /// <returns>CSS positioning or size text derived from the current display state.</returns>
    private string GetTerminalStyle(string tabId)
    {
        return _activeTab == tabId
            ? "position: absolute; inset: 0; z-index: 1; visibility: visible;"
            : "position: absolute; inset: 0; z-index: 0; visibility: hidden;";
    }


    /// <summary>
    ///     Resolves visibility styling for the build terminal.
    /// </summary>
    /// <returns>CSS positioning or size text derived from the current display state.</returns>
    private string GetBuildTerminalStyle()
    {
        return GetTerminalStyle("build");
    }


    /// <summary>
    ///     Resolves visibility styling for the web terminal.
    /// </summary>
    /// <returns>CSS positioning or size text derived from the current display state.</returns>
    private string GetWebTerminalStyle()
    {
        return GetTerminalStyle("web");
    }


    /// <summary>
    ///     Resolves visibility styling for a database terminal.
    /// </summary>
    /// <param name="tabId">Stable terminal tab identity.</param>
    /// <returns>CSS positioning or size text derived from the current display state.</returns>
    private string GetDatabaseTerminalStyle(string tabId)
    {
        return GetTerminalStyle(tabId);
    }


    /// <summary>
    ///     Returns the current recorded database terminal tabs.
    /// </summary>
    /// <returns>The current database tab sequence, which may be empty.</returns>
    private IEnumerable<DatabaseTab> GetDatabaseTabs()
    {
        return _databaseTabs;
    }


    /// <summary>
    ///     Gets a compact label for the selected container in the metrics panel.
    /// </summary>
    /// <param name="containerName">Recorded container or resource identity.</param>
    /// <returns>An uppercase initial for a known resource, or the fallback label for missing input.</returns>
    private static string GetContainerInitial(string containerName)
    {
        return string.IsNullOrWhiteSpace(containerName)
            ? "#"
            : char.ToUpperInvariant(containerName.Trim()[0]).ToString(CultureInfo.InvariantCulture);
    }


    /// <summary>
    ///     Formats the metric track using the shared legacy presentation rules.
    /// </summary>
    /// <param name="metricValue">The existing observed metric display text.</param>
    /// <param name="isMemoryMetric">Whether the value contains used and limit memory quantities.</param>
    /// <returns>The existing bounded CSS track width.</returns>
    private static string GetMetricTrackStyle(string metricValue, bool isMemoryMetric)
    {
        return DeploymentMetricDisplay.TrackStyle(metricValue, isMemoryMetric);
    }

    /// <summary>
    ///     Connects live diagnostics when the project view is ready and rejoins after route changes.
    /// </summary>
    /// <param name="firstRender">Indicates whether this is the first time the component is being rendered.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        switch (_isLoading)
        {
            // OnInitializedAsync may yield while the loading view is rendered. The first render
            // therefore does not guarantee that any Terminal component exists yet.
            case false when _app is not null && !_hubInitializationStarted:
            {
                _hubInitializationStarted = true;
                if (_currentUserId == Guid.Empty)
                {
                    Logger.LogWarning(
                        "Skipping log hub connection for project {ProjectId} because no user ID was resolved.",
                        ProjectId);
                    return;
                }

                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(NavigationManager.ToAbsoluteUri("/loghub"))
                    .WithAutomaticReconnect()
                    .Build();

                // Report transient disconnects while keeping the current terminal contents intact.
                _hubConnection.Reconnecting += exception =>
                {
                    Logger.LogWarning(
                        "Log hub connection is reconnecting for project {ProjectId}. Failure {FailureType}.",
                        ProjectId, exception?.GetType().Name);
                    return Task.CompletedTask;
                };

                // SignalR group membership is connection-scoped, so reauthorize after reconnecting.
                _hubConnection.Reconnected += async _ =>
                {
                    try
                    {
                        await JoinLogHubGroupAsync();
                        Logger.LogInformation("Log hub connection rejoined the project group for project {ProjectId}.",
                            ProjectId);
                    }
                    catch (OperationCanceledException) when (_cloudPollCancellation.IsCancellationRequested)
                    {
                        // Navigation ended this page's subscription; no UI remains to report a failure to.
                    }
                    catch (Exception exception)
                    {
                        Logger.LogWarning(
                            "Log hub connection could not rejoin the project group for project {ProjectId}. Failure {FailureType}.",
                            ProjectId, exception.GetType().Name);
                        await InvokeAsync(async () =>
                        {
                            _terminalReplayPending = false;
                            await WriteTerminalNoticeAsync("Log history is temporarily unavailable. Reload to retry.");
                        });
                    }
                };

                _hubConnection.On<DeploymentTerminalLog>("ReceiveTerminalLog", async terminalLog =>
                {
                    await InvokeAsync(async () =>
                    {
                        if (_analysisDisposed || !CanReplayTerminalHistory() ||
                            terminalLog.DeploymentId != _terminalDeploymentId) return;
                        if (_terminalReplayPending)
                        {
                            _terminalReplay.Buffer(terminalLog);
                            return;
                        }

                        await WriteTerminalLogAsync(terminalLog);
                    });
                });

                _hubConnection.On<string>("ReceiveTerminalNotice", message =>
                    InvokeAsync(() => WriteTerminalNoticeAsync(message)));

                _hubConnection.On<DeploymentRuntimeMetricSnapshot>("ReceiveDeploymentMetrics", snapshot =>
                    InvokeAsync(() =>
                    {
                        if (_analysisDisposed || !CanReplayTerminalHistory() || snapshot.ProjectId != ProjectId ||
                            snapshot.DeploymentId != _terminalDeploymentId) return;
                        ApplyTypedMetrics(snapshot.ResourceName, snapshot.Samples.Select(s => new DeploymentMetricPoint(
                            snapshot.ResourceName, s.Name, s.Unit, snapshot.Timestamp, s.Value, s.Value, s.Value)));
                        _showSavedMetricNotice = false;
                        StateHasChanged();
                    }));

                _hubConnection.On<string, string, string>("ReceiveContainerMetrics",
                    (containerName, cpuUsage, memoryUsage) =>
                    {
                        return InvokeAsync(() =>
                        {
                            if (_analysisDisposed || !CanReplayTerminalHistory() ||
                                _typedMetrics.ContainsKey(containerName)) return;
                            _containerMetrics[containerName] = (cpuUsage, memoryUsage);
                            _showSavedMetricNotice = false;
                            if (!_metricContainerNames.Contains(containerName))
                                _metricContainerNames.Add(containerName);
                            StateHasChanged();
                        });
                    }
                );

                try
                {
                    await _hubConnection.StartAsync(_cloudPollCancellation.Token);
                    await JoinLogHubGroupAsync();
                }
                catch (OperationCanceledException) when (_cloudPollCancellation.IsCancellationRequested)
                {
                    // Reload/navigation cancels initial connection and replay without a failure notice.
                }
                catch (Exception ex)
                {
                    Logger.LogError("Failed to start SignalR connection for project {ProjectId} Failure {FailureType}.",
                        ProjectId, ex.GetType().Name);
                    _terminalReplayPending = false;
                    await WriteTerminalNoticeAsync("Log history is temporarily unavailable. Reload to retry.");
                }

                break;
            }
            case false when (_app is not null && _rejoinPending &&
                             _hubConnection?.State == HubConnectionState.Connected):
                _rejoinPending = false;
                await JoinLogHubGroupAsync();
                break;
        }
    }

    /// <summary>
    ///     Joins the project stream and restores the latest deployment's bounded saved output.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task JoinLogHubGroupAsync()
    {
        if (_analysisDisposed || _cloudPollCancellation.IsCancellationRequested || _hubConnection is null ||
            _hubConnection.State != HubConnectionState.Connected) return;

        await _terminalReplayGate.WaitAsync(_cloudPollCancellation.Token);

        try
        {
            var project = ProjectId;
            var deployment = _terminalDeploymentId;
            var generation = _pageGeneration;
            var active = CanReplayTerminalHistory();

            var protector = DataProtectionProvider.CreateProtector(LogHub.ProtectorPurpose)
                .ToTimeLimitedDataProtector();

            var secureToken = protector.Protect($"{project}:{_currentUserId}", TimeSpan.FromMinutes(5));
            _terminalReplayPending = active;

            var history = await _hubConnection.InvokeAsync<DeploymentTerminalHistory>("JoinProjectGroup",
                project, active ? deployment : null, secureToken, _terminalReplay.LastOrderId,
                _cloudPollCancellation.Token);

            await InvokeAsync(async () =>
            {
                if (!IsCurrentTerminalRead(project, deployment, generation))
                {
                    if (generation == _pageGeneration) _terminalReplayPending = false;
                    return;
                }

                // Empty history is normal. Storage errors remain visible outside deployment output.
                _lastHistoryAvailability =
                    history.Availability?.StartsWith("No saved output", StringComparison.Ordinal) == true
                        ? null
                        : history.Availability;

                if (history.EarlierOmitted)
                    _lastHistoryAvailability = _terminalReplay.LastOrderId == 0
                        ? "Showing bounded saved output across channels. Open deployment history for earlier output."
                        : "More output is available and will load on the next refresh.";

                foreach (var terminalLog in history.Events)
                {
                    if (!IsCurrentTerminalRead(project, deployment, generation))
                        return;

                    await WriteTerminalLogAsync(terminalLog, true);
                }

                if (!IsCurrentTerminalRead(project, deployment, generation))
                    return;

                if (history is { CanAdvanceCursor: true, Events.Count: > 0 })
                    _terminalReplay.Confirm(history.Events.Max(e => e.OrderId));

                foreach (var terminalLog in _terminalReplay.PendingSnapshot())
                {
                    if (!IsCurrentTerminalRead(project, deployment, generation)) return;
                    await WriteTerminalLogAsync(terminalLog);
                }

                _terminalReplay.ClearPending();
                _terminalReplayPending = false;
                if (_terminalReplay.Overflow)
                {
                    _lastHistoryAvailability = "Some live output was omitted. Reload to recover available history.";
                    _terminalReplay.AcknowledgeOverflow();
                }

                StateHasChanged();
            });
        }
        finally
        {
            _terminalReplayGate.Release();
        }

        await RestoreMetricSnapshotAsync();
    }

    /// <summary>
    ///     Restores the latest saved numeric samples after reload, without replacing newer live values.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RestoreMetricSnapshotAsync()
    {
        if (_analysisDisposed || _cloudPollCancellation.IsCancellationRequested ||
            !CanReplayTerminalHistory() || !_terminalDeploymentId.HasValue ||
            DateTimeOffset.UtcNow < _nextMetricReplayAt) return;

        _nextMetricReplayAt = DateTimeOffset.UtcNow.AddSeconds(60);

        var deploymentId = _terminalDeploymentId.Value;
        var projectId = ProjectId;
        var generation = _pageGeneration;
        var token = _cloudPollCancellation.Token;

        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();

            var end = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60);
            var history = await scope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>()
                .ReadMetricsAsync(_currentUserId, projectId, deploymentId, end.AddMinutes(-15), end, 500,
                    token);

            if (_analysisDisposed || token.IsCancellationRequested) return;
            await InvokeAsync(() =>
            {
                if (token.IsCancellationRequested ||
                    !IsCurrentTerminalRead(projectId, deploymentId, generation)) return;
                foreach (var group in history.Points.GroupBy(p => p.Container))
                {
                    ApplyTypedMetrics(group.Key, group);
                    _showSavedMetricNotice = true;
                }

                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_analysisDisposed || token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Saved metric recovery unavailable: {FailureType}.", exception.GetType().Name);
        }
    }

    /// <summary>
    ///     Merges native samples by resource and series; delayed deliveries never replace newer
    ///     observations.
    /// </summary>
    /// <param name="resource">Recorded resource name for these metric samples.</param>
    /// <param name="points">Recorded metric observations to format; missing samples remain unavailable.</param>
    private void ApplyTypedMetrics(string resource, IEnumerable<DeploymentMetricPoint> points)
    {
        if (!_typedMetrics.TryGetValue(resource, out var samples))
            _typedMetrics[resource] = samples = new Dictionary<string, DeploymentMetricPoint>();

        foreach (var point in points)
            if (double.IsFinite(point.Average) && point.Average >= 0 &&
                DeploymentMetricCatalog.Units.TryGetValue(point.Name, out var unit) && point.Unit == unit &&
                (!samples.TryGetValue(point.Name, out var current) || point.Timestamp > current.Timestamp))
                samples[point.Name] = point;

        _containerMetrics[resource] =
            DeploymentMetricDisplay.Latest(samples.Values, _app?.SourceType == SourceType.Remote);

        if (!_metricContainerNames.Contains(resource))
            _metricContainerNames.Add(resource);
    }

    /// <summary>
    ///     Clears all initialized source terminals without replacing their component instances.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="Microsoft.JSInterop.JSException">
    ///     The browser interaction fails or the interactive circuit is
    ///     disconnected.
    /// </exception>
    private async Task ClearTerminalsAsync()
    {
        foreach (var terminal in new[]
                 {
                     _buildTerminal, _webTerminal, _githubActionsTerminal,
                     _azureWebTerminal, _azureSystemTerminal, _azureDatabaseTerminal
                 }.Concat(_dbTerminals.Values))
            if (terminal is not null)
                await terminal.ClearAsync();
    }

    /// <summary>
    ///     Routes current deployment output to its channel with bounded buffering and replay
    ///     deduplication.
    /// </summary>
    /// <param name="terminalLog">Redacted deployment output with channel and replay identity.</param>
    /// <param name="isReplay">Whether the record comes from saved history.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task WriteTerminalLogAsync(DeploymentTerminalLog terminalLog, bool isReplay = false)
    {
        if (terminalLog.IsAvailabilityNotice)
        {
            await WriteTerminalNoticeAsync(terminalLog.Message);
            return;
        }

        if (!_terminalReplay.Accept(terminalLog.OrderId, isReplay)) return;
        var terminal = terminalLog.TerminalChannel switch
        {
            "build" => _buildTerminal,
            "web" => _webTerminal,
            "github-actions" => _githubActionsTerminal,
            "azure-web" => _azureWebTerminal,
            "azure-system" => _azureSystemTerminal,
            "azure-db" => _azureDatabaseTerminal,
            _ when _dbTerminals.TryGetValue(terminalLog.TerminalChannel, out var databaseTerminal) => databaseTerminal,
            _ => null
        };
        if (terminal is not null) await terminal.WriteAsync(DeploymentTerminalPresentation.Format(terminalLog));
    }

    /// <summary>
    ///     Shows operational guidance outside the terminal's deployment output.
    /// </summary>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task WriteTerminalNoticeAsync(string message)
    {
        if (!_analysisDisposed)
        {
            _lastHistoryAvailability = message;
            StateHasChanged();
        }

        return Task.CompletedTask;
    }


    /// <summary>
    ///     This method is called when the terminal component is ready. It writes an initial message to
    ///     the terminal indicating that the AutoMate Terminal has been initialized and is waiting for
    ///     deployment logs.
    /// </summary>
    /// <param name="terminal">Initialized terminal component, or null when it is not rendered.</param>
    /// <param name="componentName">Display name of the terminal channel.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task OnTerminalReady(Terminal? terminal, string componentName)
    {
        if (terminal != null && _terminalReplay.LastOrderId == 0)
        {
            await terminal.WriteLineAsync($"\x1b[1;32mAutoMate {componentName} Terminal Initialized...\x1b[0m");
            await terminal.WriteLineAsync("Waiting for logs...");
            await terminal.WriteAsync("$ ");
        }
    }


    /// <summary>
    ///     Initializes the selected database terminal and restores buffered output for that channel.
    /// </summary>
    /// <param name="tabId"></param>
    /// <param name="dbType"></param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task OnDbTerminalReady(string tabId, string dbType)
    {
        if (_dbTerminals.TryGetValue(tabId, out var terminal))
            await OnTerminalReady(terminal, dbType);
    }


    /// <summary>
    ///     Selects the preceding resource metric card with wraparound.
    /// </summary>
    private void PreviousMetric()
    {
        if (_metricContainerNames.Count == 0) return;
        _currentMetricIndex = (_currentMetricIndex - 1 + _metricContainerNames.Count) % _metricContainerNames.Count;
    }


    /// <summary>
    ///     Selects the next resource metric card with wraparound.
    /// </summary>
    private void NextMetric()
    {
        if (_metricContainerNames.Count == 0) return;
        _currentMetricIndex = (_currentMetricIndex + 1) % _metricContainerNames.Count;
    }


    /// <summary>
    ///     Retrieves the current authenticated user's ID from the authentication state. If the user is
    ///     not authenticated, it returns Guid.Empty.
    /// </summary>
    /// <returns>The user ID if authenticated, otherwise Guid.Empty.</returns>
    private async Task<Guid> GetCurrentUserIdAsync()
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        return await AuthenticatedUserResolver.GetCurrentUserIdAsync(AuthStateProvider,
            scope.ServiceProvider.GetRequiredService<IUserService>());
    }


    /// <summary>
    ///     Chooses the console layout class for the project source.
    /// </summary>
    /// <param name="sourceType">Local or remote project source.</param>
    /// <returns>The CSS classes for the selected state.</returns>
    private static string GetProjectSourceClass(SourceType sourceType)
    {
        return sourceType == SourceType.Remote ? "project-console-remote" : "project-console-local";
    }

    /// <summary>
    ///     Finds the first recorded web project in the current application.
    /// </summary>
    /// <returns>The first web project, or null when none is loaded.</returns>
    private CsProject? GetPrimaryWebProject()
    {
        return _app?.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
    }

    /// <summary>
    ///     Finds the newest deployment across the current application projects.
    /// </summary>
    /// <returns>The newest recorded deployment, or null when no deployment exists.</returns>
    private Deployment? GetLatestDeployment()
    {
        return _app?.CsProjects
            .SelectMany(csp => csp.Deployments)
            .MaxBy(deployment => deployment.CreatedAt);
    }


    /// <summary>
    ///     Formats an available timestamp for the project summary.
    /// </summary>
    /// <param name="timestamp">Recorded timestamp, if available.</param>
    /// <returns>The formatted timestamp, or "Never" when absent.</returns>
    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.ToString("yyyy. MM. dd. HH:mm") ?? "Never";
    }

    /// <summary>
    ///     Uses the Azure revision or a shortened Docker identity for the summary.
    /// </summary>
    /// <param name="deployment">Deployment identity or metadata to inspect, if available.</param>
    /// <returns>The recorded Azure revision, up to 12 Docker identity characters, or "None".</returns>
    private static string GetRevisionLabel(Deployment? deployment)
    {
        if (!string.IsNullOrWhiteSpace(deployment?.CloudContainerRevision))
            return deployment.CloudContainerRevision;

        if (!string.IsNullOrWhiteSpace(deployment?.DockerContainerId))
            return deployment.DockerContainerId.Length <= 12
                ? deployment.DockerContainerId
                : deployment.DockerContainerId[..12];

        return "None";
    }

    /// <summary>
    ///     Chooses the provider label from the current project source.
    /// </summary>
    /// <returns>"Azure" for remote projects; otherwise "Docker".</returns>
    private string GetProviderLabel()
    {
        return _app?.SourceType == SourceType.Remote ? "Azure" : "Docker";
    }

    /// <summary>
    ///     Displays the configured Azure region or discovered local web port.
    /// </summary>
    /// <param name="configuration">Deployment settings or host configuration used by this operation.</param>
    /// <returns>The recorded region or local endpoint, with a fixed pending-configuration label when unavailable.</returns>
    private string GetRegionOrEndpoint(Configuration? configuration)
    {
        if (_app?.SourceType == SourceType.Remote)
            return string.IsNullOrWhiteSpace(configuration?.CloudAzureRegion)
                ? "Configured during deploy"
                : configuration.CloudAzureRegion;

        return _webHostPort > 0 ? $"localhost:{_webHostPort}" : "Assigned during deploy";
    }


    /// <summary>
    ///     Identifies one database terminal by stable tab key and display label.
    /// </summary>
    /// <param name="TabId">Stable terminal tab identity.</param>
    /// <param name="DisplayName">Human-readable database tab label.</param>
    private record DatabaseTab(string TabId, string DisplayName);
}
