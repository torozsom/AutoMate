using System.Globalization;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Orchestration;
using Domain.DTO;
using Domain.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR.Client;
using Web.Components.Shared;
using Web.Hubs;

namespace Web.Components.Pages;

/// <summary>
///     A Blazor component that displays the details of a specific project, including its name, description,
///     and other relevant information. This component retrieves the project details based on the provided
///     ProjectId parameter and ensures that the user is authenticated before displaying the information.
/// </summary>
public partial class ProjectDetails : ComponentBase, IAsyncDisposable
{
    /// <summary>Stops database-backed status and terminal catch-up when the page closes.</summary>
    private readonly CancellationTokenSource _cloudPollCancellation = new();

    /// <summary>Prevents overlapping status refreshes in one Blazor circuit.</summary>
    private readonly SemaphoreSlim _cloudPollGate = new(1, 1);

    /// A dictionary to store the latest CPU and memory usage metrics for each container.
    private readonly Dictionary<string, (string Cpu, string Memory)> _containerMetrics = new();

    /// A dictionary to store the terminal instances for each database container.
    private readonly Dictionary<string, Terminal> _dbTerminals = new();


    /// A list of container names for which metrics are currently being displayed.
    private readonly List<string> _metricContainerNames = [];

    private readonly List<DeploymentTerminalLog> _pendingTerminalLogs = [];

    /// <summary>Bounded identities rendered ahead of the confirmed replay cursor.</summary>
    private readonly HashSet<long> _renderedTerminalIds = [];

    /// <summary>Serializes initial, reconnect and periodic replay handshakes.</summary>
    private readonly SemaphoreSlim _terminalReplayGate = new(1, 1);

    /// A string to track the currently active tab in the UI, defaulting to "build".
    private string _activeTab = "build";

    /// <summary>Prevents late page reads from publishing after disposal.</summary>
    private bool _analysisDisposed;

    /// A nullable variable to hold the app details fetched from the database.
    private Domain.Entities.Application? _app;

    /// A terminal instance for Azure Container Apps system and revision output.
    private Terminal? _azureSystemTerminal;

    /// A terminal instance for Azure Container Apps console output.
    private Terminal? _azureWebTerminal;

    /// <summary>Project bound to this component's current subscription.</summary>
    private Guid _boundProjectId;

    /// A terminal instance for displaying build logs.
    private Terminal? _buildTerminal;

    /// <summary>Background refresh task for multi-instance SaaS deployments.</summary>
    private Task? _cloudPollTask;

    /// <summary>Newest durable SaaS run displayed by this page.</summary>
    private CloudDeploymentReceipt? _cloudRunReceipt;

    /// A nullable variable to hold the current deployment configuration when the user initiates a deployment.
    private DeploymentConfigDto? _currentDeployConfig;

    /// The index of the currently displayed container's metrics in the list of container names.
    private int _currentMetricIndex;

    /// The internal AutoMate user ID resolved once during component initialization.
    private Guid _currentUserId;

    /// A list of database tabs to be displayed in the UI, initialized as an empty list.
    private IEnumerable<DatabaseTab> _databaseTabs = [];

    /// A terminal instance for GitHub Actions output from a cloud deployment.
    private Terminal? _githubActionsTerminal;

    /// A nullable variable to hold the SignalR hub connection for receiving real-time logs and metrics.
    private HubConnection? _hubConnection;

    private bool _hubInitializationStarted;


    /// A boolean flag to indicate whether the project is currently being deployed.
    private bool _isDeploying;

    /// A boolean flag to indicate whether the component is currently loading data.
    private bool _isLoading = true;

    /// A boolean flag to indicate whether the deployment process is currently being stopped.
    private bool _isStopping;

    /// <summary>Suppresses unchanged storage-status notices during periodic catch-up.</summary>
    private string? _lastHistoryAvailability;

    private long _lastTerminalOrderId;

    /// <summary>Bounds saved metric recovery while waiting for the first live sample.</summary>
    private DateTimeOffset _nextMetricReplayAt;

    /// <summary>Rejects terminal replies overtaken by navigation or a deployment replacement.</summary>
    private long _pageGeneration;

    /// <summary>Stable key used if the current SaaS admission request is retried.</summary>
    private string? _pendingCloudIdempotencyKey;

    /// <summary>Rejoins the authorized stream after the replacement route has rendered terminals.</summary>
    private bool _rejoinPending;

    /// A nullable variable to hold the path of the selected C# project when initiating a deployment.
    private string? _selectedProjectPath;

    /// <summary>Shows the deployment-scoped assessment dialog.</summary>
    private bool _showAnalysisDialog;

    /// A boolean flag to control the visibility of the deployment configuration modal.
    private bool _showConfigModal;

    /// <summary>Distinguishes recovered observations from fresh live samples.</summary>
    private bool _showSavedMetricNotice;

    private bool _terminalBufferOverflow;
    private Guid? _terminalDeploymentId;
    private bool _terminalReplayPending = true;

    /// The actual host port currently bound to the web container.
    private int _webHostPort;

    /// A nullable variable to hold the terminal instance for displaying web container logs.
    private Terminal? _webTerminal;

    /// A short workflow status message displayed for remote cloud deployments.
    private string? _workflowStatusMessage;

    /// The GitHub Actions workflow URL for the latest cloud deployment, when available.
    private string? _workflowUrl;

    /// The ID of the project to be displayed, passed as a parameter to the component.
    [Parameter]
    public Guid ProjectId { get; set; }

    /// The project service used to fetch project details and manage project-related operations.
    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;

    /// The authentication state provider used to retrieve the current user's authentication state and claims.
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// Queue that hands deployment work to the hosted background worker.
    [Inject]
    private IDeploymentJobQueue DeploymentJobQueue { get; set; } = null!;

    /// <summary>Durable SaaS cloud admission.</summary>
    [Inject]
    private ICloudDeploymentRunService CloudDeploymentRuns { get; set; } = null!;

    /// <summary>Creates an independent read scope for each cross-instance status poll.</summary>
    [Inject]
    private IServiceScopeFactory ScopeFactory { get; set; } = null!;

    /// The user service used to retrieve user details and manage user-related operations.
    [Inject]
    private IUserService UserService { get; set; } = null!;

    /// The navigation manager used to navigate between pages and handle URL changes.
    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    /// The data protection provider used to protect sensitive data.
    [Inject]
    private IDataProtectionProvider DataProtectionProvider { get; set; } = null!;

    /// The project scanner service used to analyze project dependencies and prepare deployment configurations.
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

    /// The deployment orchestrator service used to manage Docker deployments.
    [Inject]
    private IDeploymentStatusNotifier DeploymentStatusNotifier { get; set; } = null!;

    /// The Docker service used to interact with the Docker daemon and manage Docker containers.
    [Inject]
    private IDockerService DockerService { get; set; } = null!;

    [Inject] private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// The logger used to log information and errors related to the project details component.
    [Inject]
    private ILogger<ProjectDetails> Logger { get; set; } = null!;

    /// <summary>
    ///     Disposes of the component by leaving the SignalR group
    ///     associated with the project and disposing of the hub connection.
    /// </summary>
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

    private void OnQueueStateChanged(Guid projectId)
    {
        if (projectId == ProjectId) _ = InvokeAsync(StateHasChanged);
    }


    /// <summary>
    ///     Initiates the deployment process for the project by analyzing
    ///     its dependencies and preparing the deployment configuration.
    /// </summary>
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
                ex?.GetType().Name);
            _isStopping = false;
            StateHasChanged();
        }
    }


    /// <summary>
    ///     Hides the deployment configuration modal and resets the related state variables to their default values.
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
    ///     Executes the deployment process asynchronously by hiding the configuration modal,
    ///     setting the deploying state, and invoking the deployment orchestrator to deploy
    ///     the project with the specified configuration.
    /// </summary>
    /// <param name="finalConfig">The deployment configuration to be used for the project deployment.</param>
    private async Task ExecuteDeploymentAsync(DeploymentConfigDto finalConfig)
    {
        HideConfigModal();
        _isDeploying = true;
        _webHostPort = 0;
        _workflowStatusMessage = null;
        _workflowUrl = null;
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
                finalConfig.ProjectId, ex?.GetType().Name);
            _workflowStatusMessage = ex is InvalidOperationException &&
                                     ex.Message == "The deployment queue is full. Try again after a job starts."
                ? "The deployment queue is full. Try again after a job starts."
                : "AutoMate could not queue this deployment. Try again shortly.";
            _isDeploying = false;
            StateHasChanged();
        }
    }

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

        var azureCredentials = await UserService.GetAzureCloudCredentialsAsync(_currentUserId);
        var userDetails = await GetCurrentUserDetailsAsync();

        if (azureCredentials == null || string.IsNullOrWhiteSpace(userDetails.AccessToken))
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
                _cloudRunReceipt = await CloudDeploymentRuns.StartAsync(new CloudDeploymentStart(
                    _currentUserId, finalConfig.ProjectId, _pendingCloudIdempotencyKey,
                    repository.Owner, repository.Name, userDetails.AccessToken,
                    finalConfig, CloudDeploymentPageDefaults.CreateRemoteProjectMetadata(),
                    _app.Name, "."));
                _pendingCloudIdempotencyKey = null;
                UpdateCloudRunMessage();
            }
            else
            {
                await DeploymentJobQueue.EnqueueAsync(new CloudDeploymentJob(new CloudDeploymentRequestDto
                {
                    RequestingUserId = _currentUserId,
                    Config = finalConfig,
                    Metadata = CloudDeploymentPageDefaults.CreateRemoteProjectMetadata(),
                    CsProjectName = _app.Name,
                    RepositoryRoot = ".",
                    GitHubAccessToken = userDetails.AccessToken,
                    GitHubContainerRegistryToken = userDetails.AccessToken,
                    AzureCredentials = azureCredentials,
                    RepositoryOwner = repository.Owner,
                    RepositoryName = repository.Name
                }));
            }

            _workflowStatusMessage = "GitHub Actions workflow has been queued.";
            _workflowUrl = $"https://github.com/{repository.Owner}/{repository.Name}/actions/workflows/deploy.yml";
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to queue cloud deployment for project {ProjectId} Failure {FailureType}.",
                finalConfig.ProjectId, ex?.GetType().Name);
            _workflowStatusMessage = "Cloud deployment failed to queue. Verify provider access and try again shortly.";
            _isDeploying = false;
            StateHasChanged();
        }
    }


    /// <summary>
    ///     Asynchronously initializes the component by retrieving the current user's ID and fetching the project details
    ///     based on the provided ProjectId. If the project is found, it analyzes the project's dependencies to determine
    ///     the databases used and prepares the database tabs for display.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        DeploymentStatusNotifier.OnStatusChanged += OnDeploymentStatusChanged;
        DeploymentJobQueue.StateChanged += OnQueueStateChanged;
        _boundProjectId = ProjectId;
        _currentUserId = await GetCurrentUserIdAsync();
        await LoadCurrentProjectAsync();
        if (_app is not null) _cloudPollTask = PollTerminalHistoryAsync(_cloudPollCancellation.Token);
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (_boundProjectId == Guid.Empty)
        {
            _boundProjectId = ProjectId;
            return;
        }

        if (_boundProjectId == ProjectId || _analysisDisposed) return;
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

    /// <summary>Loads route-specific data while fencing old route responses at every I/O boundary.</summary>
    private async Task LoadCurrentProjectAsync()
    {
        var project = ProjectId;
        var generation = _pageGeneration;
        var token = _cloudPollCancellation.Token;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var app = _currentUserId == Guid.Empty
            ? null
            : await scope.ServiceProvider
                .GetRequiredService<IApplicationService>().GetAppByIdAsync(project, _currentUserId, token);
        if (_analysisDisposed || token.IsCancellationRequested || project != ProjectId ||
            generation != _pageGeneration) return;
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
            _databaseTabs = config.Databases.Select(db => new DatabaseTab(db.DbType, db.ContainerNameSuffix, db.DbType))
                .ToList();
        }

        await UpdateWebHostPortAsync();
        if (project == ProjectId && generation == _pageGeneration && !token.IsCancellationRequested) _isLoading = false;
    }

    /// <summary>Refreshes the current route through independent scopes and confirms active terminal replay progress.</summary>
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

    /// <summary>Shows durable queue age and workflow phase after reloads and cross-instance updates.</summary>
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
    ///     Event handler that is called when the deployment status changes. It checks if
    ///     the status change is related to the current project, and if so, it updates the
    ///     latest deployment status in the project details and triggers a UI refresh.
    /// </summary>
    /// <param name="projectId">The ID of the project for which the status has changed.</param>
    /// <param name="status">The new deployment status.</param>
    private void OnDeploymentStatusChanged(Guid projectId, DeploymentStatus status)
    {
        _ = InvokeAsync(() => HandleDeploymentStatusChangedAsync(projectId, status));
    }


    /// <summary>
    ///     Handles the deployment status change by checking if the status change is relevant to the current
    ///     project, updating the latest deployment status, and refreshing the project details if necessary.
    /// </summary>
    /// <param name="projectId">The ID of the project for which the status has changed.</param>
    /// <param name="status">The new deployment status.</param>
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
    ///     Updates the exposed port for the web container by checking the latest deployment status and retrieving
    ///     the host port mapped to the web container from the Docker service if the deployment is running.
    /// </summary>
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
                if (_analysisDisposed || project != ProjectId || generation != _pageGeneration) return;
                if (hostPort > 0)
                {
                    _webHostPort = hostPort;
                    return;
                }
            }

            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(1));
        }

        _webHostPort = 0;
    }


    /// <summary>
    ///     Refreshes the project details by re-fetching the project from the database.
    /// </summary>
    private async Task RefreshProjectAsync(bool resolveWebPortWithRetry = false)
    {
        if (_currentUserId == Guid.Empty || _analysisDisposed || _isLoading) return;
        var project = ProjectId;
        var generation = _pageGeneration;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var app = await scope.ServiceProvider.GetRequiredService<IApplicationService>()
            .GetAppByIdAsync(project, _currentUserId, _cloudPollCancellation.Token);
        if (_analysisDisposed || generation != _pageGeneration || project != ProjectId) return;
        var previouslyActive = CanReplayTerminalHistory();
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
            _pendingTerminalLogs.Clear();
            _terminalReplayPending = CanReplayTerminalHistory();
        }

        await UpdateWebHostPortAsync(resolveWebPortWithRetry ? 6 : 1);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Resets replay and metric state for a replacement deployment or route.</summary>
    private void ResetTerminalState()
    {
        _containerMetrics.Clear();
        _metricContainerNames.Clear();
        _currentMetricIndex = 0;
        _nextMetricReplayAt = DateTimeOffset.MinValue;
        _showSavedMetricNotice = false;
        _lastTerminalOrderId = 0;
        _renderedTerminalIds.Clear();
        _lastHistoryAvailability = null;
        _pendingTerminalLogs.Clear();
        _terminalBufferOverflow = false;
        _terminalReplayPending = true;
    }

    /// <summary>Opens context configuration and saved results for the current deployment.</summary>
    private void OpenAnalysisDialog()
    {
        if (!_analysisDisposed && GetLatestDeployment() is not null) _showAnalysisDialog = true;
    }

    /// <summary>Closes presentation without canceling durable analysis work.</summary>
    private void CloseAnalysisDialog()
    {
        _showAnalysisDialog = false;
    }

    /// <summary>Only the currently active deployment can replay output on the live project page.</summary>
    private bool CanReplayTerminalHistory()
    {
        return GetLatestStatus() is DeploymentStatus.Starting or DeploymentStatus.Running;
    }

    /// <summary>Rejects replies after disposal, navigation, deployment replacement or an inactive status.</summary>
    private bool IsCurrentTerminalRead(Guid project, Guid? deployment, long generation)
    {
        return !_analysisDisposed && generation == _pageGeneration && project == ProjectId &&
               deployment == _terminalDeploymentId && CanReplayTerminalHistory();
    }

    /// <summary>
    ///     Retrieves the latest deployment status for the project by
    ///     looking at the most recent deployment across all C# projects.
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
    private static DeploymentConfigDto CreateCloudDeploymentConfig(Domain.Entities.Application app)
    {
        return CloudDeploymentPageDefaults.CreateConfiguration(app);
    }


    /// <summary>
    ///     Mirrors the Docker Compose template's web container name convention.
    /// </summary>
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


    /// Sets the active tab in the UI based on the provided tab ID.
    private void SetActiveTab(string tabId)
    {
        _activeTab = tabId;
    }


    /// Retrieves the CSS class for a tab based on whether it is the active tab or not.
    private string GetTabClass(string tabId)
    {
        return _activeTab == tabId
            ? "terminal-tab-link active"
            : "terminal-tab-link";
    }


    private string GetBuildTabClass()
    {
        return $"nav-link {GetTabClass("build")}";
    }


    private string GetWebTabClass()
    {
        return $"nav-link {GetTabClass("web")}";
    }


    /// Retrieves the inline style for a terminal based on whether its corresponding tab is active or not.
    private string GetTerminalStyle(string tabId)
    {
        return _activeTab == tabId
            ? "position: absolute; inset: 0; z-index: 1; visibility: visible;"
            : "position: absolute; inset: 0; z-index: 0; visibility: hidden;";
    }


    /// Retrieves the inline style for the build terminal.
    private string GetBuildTerminalStyle()
    {
        return GetTerminalStyle("build");
    }


    /// Retrieves the inline style for the web terminal.
    private string GetWebTerminalStyle()
    {
        return GetTerminalStyle("web");
    }


    /// Retrieves the inline style for a database terminal based on its corresponding tab ID.
    private string GetDatabaseTerminalStyle(string tabId)
    {
        return GetTerminalStyle(tabId);
    }


    /// Retrieves the list of database tabs to be displayed in the UI.
    private IEnumerable<DatabaseTab> GetDatabaseTabs()
    {
        return _databaseTabs;
    }


    /// <summary>
    ///     Gets a compact label for the selected container in the metrics panel.
    /// </summary>
    private static string GetContainerInitial(string containerName)
    {
        return string.IsNullOrWhiteSpace(containerName)
            ? "#"
            : char.ToUpperInvariant(containerName.Trim()[0]).ToString(CultureInfo.InvariantCulture);
    }


    /// <summary>
    ///     Converts metric text into a CSS width value for the lightweight utilization bar.
    /// </summary>
    private static string GetMetricTrackStyle(string metricValue, bool isMemoryMetric)
    {
        var percent = isMemoryMetric
            ? TryCalculateMemoryUsagePercent(metricValue)
            : TryParsePercentage(metricValue);

        if (percent is null)
            return "width: 0%;";

        var normalized = Math.Clamp(percent.Value, 0, 100);
        if (normalized > 0 && normalized < 1)
            normalized = 1;

        return $"width: {normalized.ToString("0.##", CultureInfo.InvariantCulture)}%;";
    }


    private static decimal? TryParsePercentage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var percentIndex = value.IndexOf('%', StringComparison.Ordinal);
        var numberPart = percentIndex >= 0 ? value[..percentIndex] : value;

        return decimal.TryParse(numberPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }


    private static decimal? TryCalculateMemoryUsagePercent(string memoryUsage)
    {
        var parts = memoryUsage.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;

        var usedBytes = TryParseDataSize(parts[0]);
        var limitBytes = TryParseDataSize(parts[1]);

        if (usedBytes is null || limitBytes is null || limitBytes <= 0)
            return null;

        return usedBytes.Value / limitBytes.Value * 100;
    }


    private static decimal? TryParseDataSize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var numberLength = 0;

        while (numberLength < trimmed.Length &&
               (char.IsDigit(trimmed[numberLength]) || trimmed[numberLength] is '.' or ','))
            numberLength++;

        if (numberLength == 0)
            return null;

        var numberPart = trimmed[..numberLength].Replace(',', '.');
        if (!decimal.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            return null;

        var unit = trimmed[numberLength..].Trim().ToUpperInvariant();
        var multiplier = unit switch
        {
            "B" or "" => 1m,
            "KB" => 1_000m,
            "KIB" => 1_024m,
            "MB" => 1_000_000m,
            "MIB" => 1_048_576m,
            "GB" => 1_000_000_000m,
            "GIB" => 1_073_741_824m,
            _ => 1m
        };

        return amount * multiplier;
    }


    /// <summary>
    ///     Executes logic after the component has been rendered. On the first render, establishes a SignalR connection
    ///     to the server for real-time updates and joins a project-specific group using a secure token. Registers handlers
    ///     for receiving build and container logs. This method is only executed during the first render to set up necessary
    ///     resources for the project details page.
    /// </summary>
    /// <param name="firstRender">Indicates whether this is the first time the component is being rendered.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // OnInitializedAsync may yield while the loading view is rendered. The first render
        // therefore does not guarantee that any Terminal component exists yet.
        if (!_isLoading && _app is not null && !_hubInitializationStarted)
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
                Logger.LogWarning("Log hub connection is reconnecting for project {ProjectId}. Failure {FailureType}.",
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
                        ProjectId, exception?.GetType().Name);
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
                        if (_pendingTerminalLogs.Count < 512) _pendingTerminalLogs.Add(terminalLog);
                        else _terminalBufferOverflow = true;
                        return;
                    }

                    await WriteTerminalLogAsync(terminalLog);
                });
            });

            _hubConnection.On<string>("ReceiveTerminalNotice", message =>
                InvokeAsync(() => WriteTerminalNoticeAsync(message)));

            _hubConnection.On<string, string, string>("ReceiveContainerMetrics",
                (containerName, cpuUsage, memoryUsage) =>
                {
                    return InvokeAsync(() =>
                    {
                        if (_analysisDisposed || !CanReplayTerminalHistory()) return;
                        _containerMetrics[containerName] = (cpuUsage, memoryUsage);
                        _showSavedMetricNotice = false;
                        if (!_metricContainerNames.Contains(containerName)) _metricContainerNames.Add(containerName);
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
                    ProjectId, ex?.GetType().Name);
                _terminalReplayPending = false;
                await WriteTerminalNoticeAsync("Log history is temporarily unavailable. Reload to retry.");
            }
        }
        else if (!_isLoading && _app is not null && _rejoinPending &&
                 _hubConnection?.State == HubConnectionState.Connected)
        {
            _rejoinPending = false;
            await JoinLogHubGroupAsync();
        }
    }

    /// <summary>Joins the live stream and restores only the current active deployment's bounded output.</summary>
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
                project, active ? deployment : null, secureToken, _lastTerminalOrderId, _cloudPollCancellation.Token);
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
                    _lastHistoryAvailability = _lastTerminalOrderId == 0
                        ? "Earlier output omitted; showing the most recent 500 events."
                        : "More output is available and will load on the next refresh.";
                foreach (var terminalLog in history.Events)
                {
                    if (!IsCurrentTerminalRead(project, deployment, generation)) return;
                    await WriteTerminalLogAsync(terminalLog, true);
                }

                if (!IsCurrentTerminalRead(project, deployment, generation)) return;
                if (history.CanAdvanceCursor && history.Events.Count > 0)
                {
                    _lastTerminalOrderId = Math.Max(_lastTerminalOrderId, history.Events.Max(e => e.OrderId));
                    _renderedTerminalIds.RemoveWhere(id => id <= _lastTerminalOrderId);
                }

                foreach (var terminalLog in _pendingTerminalLogs.OrderBy(item => item.OrderId).ToArray())
                {
                    if (!IsCurrentTerminalRead(project, deployment, generation)) return;
                    await WriteTerminalLogAsync(terminalLog);
                }

                _pendingTerminalLogs.Clear();
                _terminalReplayPending = false;
                if (_terminalBufferOverflow)
                {
                    _lastHistoryAvailability = "Some live output was omitted. Reload to recover available history.";
                    _terminalBufferOverflow = false;
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

    /// <summary>Restores the latest saved numeric samples after reload, without replacing newer live values.</summary>
    private async Task RestoreMetricSnapshotAsync()
    {
        if (_analysisDisposed || _cloudPollCancellation.IsCancellationRequested ||
            !CanReplayTerminalHistory() || !_terminalDeploymentId.HasValue || _containerMetrics.Count > 0 ||
            DateTimeOffset.UtcNow < _nextMetricReplayAt) return;
        _nextMetricReplayAt = DateTimeOffset.UtcNow.AddSeconds(60);
        var deploymentId = _terminalDeploymentId.Value;
        var projectId = ProjectId;
        var generation = _pageGeneration;
        var token = _cloudPollCancellation.Token;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var end = DateTimeOffset.UtcNow;
            var history = await scope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>()
                .ReadMetricsAsync(_currentUserId, projectId, deploymentId, end.AddMinutes(-15), end, 15,
                    token);
            if (_analysisDisposed || token.IsCancellationRequested) return;
            await InvokeAsync(() =>
            {
                if (token.IsCancellationRequested ||
                    !IsCurrentTerminalRead(projectId, deploymentId, generation)) return;
                foreach (var group in history.Points.GroupBy(p => p.Container))
                {
                    if (_containerMetrics.ContainsKey(group.Key)) continue;
                    _containerMetrics[group.Key] = DeploymentMetricDisplay.Latest(group);
                    if (!_metricContainerNames.Contains(group.Key)) _metricContainerNames.Add(group.Key);
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

    private async Task ClearTerminalsAsync()
    {
        foreach (var terminal in new[]
                 {
                     _buildTerminal, _webTerminal, _githubActionsTerminal,
                     _azureWebTerminal, _azureSystemTerminal
                 }.Concat(_dbTerminals.Values))
            if (terminal is not null)
                await terminal.ClearAsync();
    }

    private async Task WriteTerminalLogAsync(DeploymentTerminalLog terminalLog, bool isReplay = false)
    {
        var liveOnly = terminalLog.OrderId < 0;
        if (!liveOnly && (terminalLog.OrderId <= _lastTerminalOrderId ||
                          _renderedTerminalIds.Contains(terminalLog.OrderId))) return;
        if (!liveOnly && _renderedTerminalIds.Count >= 2000 && !isReplay)
        {
            _terminalBufferOverflow = true;
            return;
        }

        if (!liveOnly && _renderedTerminalIds.Count < 2000) _renderedTerminalIds.Add(terminalLog.OrderId);
        var terminal = terminalLog.TerminalChannel switch
        {
            "build" => _buildTerminal,
            "web" => _webTerminal,
            "github-actions" => _githubActionsTerminal,
            "azure-web" => _azureWebTerminal,
            "azure-system" => _azureSystemTerminal,
            _ when _dbTerminals.TryGetValue(terminalLog.TerminalChannel, out var databaseTerminal) => databaseTerminal,
            _ => null
        };
        if (terminal is not null) await terminal.WriteAsync(DeploymentTerminalPresentation.Format(terminalLog));
    }

    /// <summary>Shows operational guidance outside the terminal's deployment output.</summary>
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
    ///     This method is called when the terminal component is ready. It writes an initial message to the terminal
    ///     indicating that the AutoMate Terminal has been initialized and is waiting for deployment logs.
    /// </summary>
    private async Task OnTerminalReady(Terminal? terminal, string componentName)
    {
        if (terminal != null && _lastTerminalOrderId == 0)
        {
            await terminal.WriteLineAsync($"\x1b[1;32mAutoMate {componentName} Terminal Initialized...\x1b[0m");
            await terminal.WriteLineAsync("Waiting for logs...");
            await terminal.WriteAsync("$ ");
        }
    }


    /// <summary>
    ///     This method is called when the build terminal component is ready. It calls the
    ///     OnTerminalReady method with the build terminal instance and the component name "Build".
    /// </summary>
    /// <param name="tabId"></param>
    /// <param name="dbType"></param>
    private async Task OnDbTerminalReady(string tabId, string dbType)
    {
        if (_dbTerminals.TryGetValue(tabId, out var terminal))
            await OnTerminalReady(terminal, dbType);
    }


    /// Switches to the previous container's metrics.
    private void PreviousMetric()
    {
        if (_metricContainerNames.Count == 0) return;
        _currentMetricIndex = (_currentMetricIndex - 1 + _metricContainerNames.Count) % _metricContainerNames.Count;
    }


    /// Switches to the next container's metrics.
    private void NextMetric()
    {
        if (_metricContainerNames.Count == 0) return;
        _currentMetricIndex = (_currentMetricIndex + 1) % _metricContainerNames.Count;
    }


    /// <summary>
    ///     Retrieves the current authenticated user's ID from the authentication state.
    ///     If the user is not authenticated, it returns Guid.Empty.
    /// </summary>
    /// <returns>The user ID if authenticated, otherwise Guid.Empty.</returns>
    private async Task<Guid> GetCurrentUserIdAsync()
    {
        return await AuthenticatedUserResolver.GetCurrentUserIdAsync(AuthStateProvider, UserService);
    }


    /// <summary>
    ///     Gets the current user details, including the GitHub token for remote deployments.
    /// </summary>
    private async Task<(Guid UserId, string? AccessToken, bool IsGitHubUser)> GetCurrentUserDetailsAsync()
    {
        var details = await AuthenticatedUserResolver.GetCurrentUserDetailsAsync(AuthStateProvider, UserService);
        return (details.UserId, details.AccessToken, details.IsGitHubUser);
    }


    /// A record type representing a database tab in the UI.
    private record DatabaseTab(string Provider, string TabId, string DisplayName);
}