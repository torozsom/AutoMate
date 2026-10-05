using System.Collections.Concurrent;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>Host-owned supervision; every subscription is tracked, canceled and awaited on replacement/shutdown.</summary>
public sealed class LocalDeploymentLogStreamManager(
    IServiceScopeFactory scopes,
    ILogger<LocalDeploymentLogStreamManager> logger) : BackgroundService, ILocalDeploymentDiagnostics
{
    /// <summary>Protects registry mutations; source shutdown is awaited outside this shared gate.</summary>
    private readonly SemaphoreSlim _changes = new(1);

    /// <summary>Links all registered targets to host shutdown.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Finite per-project registry of supervisors.</summary>
    private readonly ConcurrentDictionary<Guid, TargetState> _targets = new();

    /// <summary>Ensures singleton/host registrations can safely dispose the same owner more than once.</summary>
    private int _disposed;

    /// <inheritdoc />
    public bool IsActive(Guid projectId, Guid deploymentId)
    {
        return _targets.TryGetValue(projectId, out var state) &&
               state.Target.DeploymentId == deploymentId && !state.Task.IsCompleted;
    }

    /// <inheritdoc />
    public async Task RegisterAsync(DockerDeploymentTarget target, bool deploymentOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ProjectId == Guid.Empty || target.DeploymentId == Guid.Empty ||
            target.Containers.Count is < 1 or > 32)
            throw new ArgumentException("Local diagnostics require ownership and a bounded container inventory.",
                nameof(target));
        TargetState state;
        while (true)
        {
            Task? retiring = null;
            await _changes.WaitAsync(cancellationToken);
            try
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                if (_targets.TryGetValue(target.ProjectId, out var existing))
                {
                    if (existing.StopTask is null && existing.Target.DeploymentId == target.DeploymentId &&
                        !existing.Task.IsCompleted &&
                        existing.Operation == deploymentOperation)
                    {
                        existing.Operation = deploymentOperation;
                        return;
                    }

                    retiring = existing.StopTask ??= StopTargetAsync(existing);
                    if (retiring.IsCompleted) _targets.TryRemove(target.ProjectId, out _);
                }

                if (retiring is null || retiring.IsCompleted)
                {
                    foreach (var completed in _targets.Where(t => t.Value.Task.IsCompleted).ToArray())
                        if (completed.Value.StopTask is null && _targets.TryRemove(completed.Key, out var removed))
                            removed.Cancellation.Dispose();
                    if (_targets.Count >= 1024)
                        throw new InvalidOperationException("Local collector capacity has been reached.");
                    state = new TargetState(target, deploymentOperation,
                        CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
                    _targets[target.ProjectId] = state;
                    state.Task = RunTargetAsync(state);
                    break;
                }
            }
            finally
            {
                _changes.Release();
            }

            // Retain the retiring target until shutdown completes, without blocking other registry entries.
            await retiring!.WaitAsync(cancellationToken);
        }

        // Registration-time overlap recovers events even when the daemon cannot connect before Compose starts.
        if (deploymentOperation)
            try
            {
                await state.Ready.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Docker lifecycle startup is delayed; timestamp recovery remains active.");
            }
    }

    /// <inheritdoc />
    public Task SetOperationAsync(Guid projectId, Guid deploymentId, bool active,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_targets.TryGetValue(projectId, out var state) && state.Target.DeploymentId == deploymentId)
            state.Operation = active;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        TargetState? state;
        Task? retirement;
        await _changes.WaitAsync(cancellationToken);
        try
        {
            _targets.TryGetValue(projectId, out state);
            retirement = state is null ? null : state.StopTask ??= StopTargetAsync(state);
        }
        finally
        {
            _changes.Release();
        }

        if (retirement is null) return;
        await retirement.WaitAsync(cancellationToken);
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (_targets.TryGetValue(projectId, out var current) && ReferenceEquals(current, state))
                _targets.TryRemove(projectId, out _);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Local diagnostics are shutting down.");
        }
        finally
        {
            await _shutdown.CancelAsync();
            await _changes.WaitAsync();
            try
            {
                foreach (var state in _targets.Values) await (state.StopTask ??= StopTargetAsync(state));
                _targets.Clear();
            }
            finally
            {
                _changes.Release();
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        base.Dispose();
        _shutdown.Dispose();
        _changes.Dispose();
    }

    /// <summary>Separates source lifetimes and viewing consent while tracking every child task.</summary>
    private async Task RunTargetAsync(TargetState state)
    {
        var token = state.Cancellation.Token;
        await using var scope = scopes.CreateAsyncScope();
        CancellationTokenSource? daemonCancellation = null;
        Task? daemon = null;
        CancellationTokenSource? runtimeCancellation = null;
        var runtime = new Dictionary<string, Task>();
        try
        {
            var source = scope.ServiceProvider.GetRequiredService<IDockerDiagnosticSource>();
            if (state.Operation)
            {
                daemonCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                daemon = source.MonitorDaemonAsync(state.Target, () => state.Ready.TrySetResult(),
                    daemonCancellation.Token);
            }
            else
            {
                state.Ready.TrySetResult();
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                var policy = await ReadPolicyAsync(state.Target, token);
                if (!policy.Current || (!state.Operation &&
                                        policy.Status is DeploymentStatus.Failed or DeploymentStatus.Stopped)) break;
                if (daemon?.IsCompleted == true)
                {
                    await CancelTasksAsync(daemonCancellation!, [daemon]);
                    daemonCancellation = null;
                    daemon = null;
                }

                var observeLifecycle = state.Operation || policy.Runtime;
                if (observeLifecycle && daemon is null)
                {
                    daemonCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    daemon = source.MonitorDaemonAsync(state.Target, () => state.Ready.TrySetResult(),
                        daemonCancellation.Token);
                }

                if (!observeLifecycle && daemon is not null)
                {
                    await CancelTasksAsync(daemonCancellation!, [daemon]);
                    daemonCancellation = null;
                    daemon = null;
                }

                if (policy.Runtime)
                {
                    runtimeCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(token);
                    foreach (var container in state.Target.Containers)
                    {
                        var logKey = container.Channel + "/logs";
                        if (!runtime.TryGetValue(logKey, out var logs) || logs.IsCompleted)
                        {
                            if (logs is not null) await ObserveTaskAsync(logs);
                            runtime[logKey] =
                                source.MonitorContainerAsync(state.Target, container, runtimeCancellation.Token);
                        }

                        var key = container.Channel + "/metrics";
                        if (!runtime.TryGetValue(key, out var metrics) || metrics.IsCompleted)
                        {
                            if (metrics is not null) await ObserveTaskAsync(metrics);
                            runtime[key] =
                                source.MonitorMetricsAsync(state.Target, container, runtimeCancellation.Token);
                        }
                    }
                }
                else if (runtimeCancellation is not null)
                {
                    await CancelTasksAsync(runtimeCancellation, runtime.Values);
                    runtime.Clear();
                    runtimeCancellation = null;
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            logger.LogDebug("Local subscriptions were canceled.");
        }
        catch (Exception exception)
        {
            logger.LogWarning("Local supervision will be restored by recovery: {FailureType}.",
                exception.GetType().Name);
        }
        finally
        {
            state.Ready.TrySetResult();
            if (runtimeCancellation is not null) await CancelTasksAsync(runtimeCancellation, runtime.Values);
            if (daemonCancellation is not null)
                await CancelTasksAsync(daemonCancellation, daemon is null ? [] : [daemon]);
        }
    }

    /// <summary>Reads current deployment and consent using an independent EF scope.</summary>
    private async Task<(bool Current, bool Runtime, DeploymentStatus Status)> ReadPolicyAsync(
        DockerDeploymentTarget target, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var latest = await scope.ServiceProvider.GetRequiredService<AutoMateDbContext>().Deployments.AsNoTracking()
            .Where(d => d.CsProject!.AppId == target.ProjectId).OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.Status, d.CsProject!.Application!.RuntimeDiagnosticsEnabled })
            .FirstOrDefaultAsync(token);
        var interest = latest?.RuntimeDiagnosticsEnabled == true || scope.ServiceProvider
            .GetRequiredService<IDeploymentRuntimeViewers>()
            .HasViewers(target.ProjectId, target.DeploymentId);
        return (latest?.Id == target.DeploymentId, interest, latest?.Status ?? DeploymentStatus.Stopped);
    }

    /// <summary>Cancels and awaits a source group before disposing its cancellation owner.</summary>
    private async Task CancelTasksAsync(CancellationTokenSource cancellation, IEnumerable<Task> tasks)
    {
        await cancellation.CancelAsync();
        await Task.WhenAll(tasks.Select(ObserveTaskAsync));
        cancellation.Dispose();
    }

    /// <summary>Observes faults without exposing provider bodies or abandoning other sources.</summary>
    private async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Docker subscription ended during cancellation.");
        }
        catch (Exception exception)
        {
            logger.LogWarning("Docker subscription ended: {FailureType}.", exception.GetType().Name);
        }
    }

    /// <summary>Releases the cancellation owner only after its supervisor and children finish.</summary>
    private async Task StopTargetAsync(TargetState state)
    {
        await state.Cancellation.CancelAsync();
        await ObserveTaskAsync(state.Task);
        state.Cancellation.Dispose();
    }

    /// <summary>Owns one deployment's tasks without retaining configuration credentials.</summary>
    private sealed class TargetState(
        DockerDeploymentTarget target,
        bool operation,
        CancellationTokenSource cancellation)
    {
        /// <summary>Whether a Compose operation requires automatic lifecycle diagnostics.</summary>
        public volatile bool Operation = operation;

        /// <summary>Immutable, non-secret inventory.</summary>
        public DockerDeploymentTarget Target { get; } = target;

        /// <summary>Cancellation linked to host shutdown.</summary>
        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>Task owned by the host registry.</summary>
        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>Single shared shutdown task; registry callers never dispose the same owner twice.</summary>
        public Task? StopTask { get; set; }

        /// <summary>First daemon subscription attempt confirmation.</summary>
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}