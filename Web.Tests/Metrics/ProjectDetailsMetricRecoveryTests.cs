using System.Reflection;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Web.Components.Pages;
using Xunit;

namespace Web.Tests;

/// <summary>
///     Verifies that pending metric reads cannot update a closed page or report expected disposal
///     as failure.
/// </summary>
/// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
public sealed class ProjectDetailsMetricRecoveryTests
{
    /// <summary>Second-by-second local observations replace restored typed history and survive delayed replay.</summary>
    [Fact]
    public void Local_live_metrics_refresh_typed_state_every_second_after_history_recovery()
    {
        var page = new ProjectDetails();
        var apply = typeof(ProjectDetails).GetMethod("ApplyTypedMetrics",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var restored = DateTimeOffset.Parse("2026-10-12T10:00:00Z");
        var state = (Dictionary<string, Dictionary<string, DeploymentMetricPoint>>)typeof(ProjectDetails)
            .GetField("_typedMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        void Receive(DateTimeOffset timestamp, double cores, double bytes) => apply.Invoke(page,
        [
            "web", new[]
            {
                new DeploymentMetricPoint("web", "automate_cpu_usage_cores", "cores", timestamp, cores, cores, cores),
                new DeploymentMetricPoint("web", "automate_memory_used_bytes", "bytes", timestamp, bytes, bytes, bytes)
            }
        ]);
        Receive(restored, 0.1, 104857600);
        for (var second = 1; second <= 5; second++)
        {
            Receive(restored.AddSeconds(second), second / 10d, 104857600 + second * 1048576);
            Assert.Equal(second / 10d, state["web"]["automate_cpu_usage_cores"].Average);
            Assert.Equal(restored.AddSeconds(second), state["web"]["automate_cpu_usage_cores"].Timestamp);
            Assert.Equal(104857600 + second * 1048576, state["web"]["automate_memory_used_bytes"].Average);
        }
        Receive(restored, 0.1, 104857600);
        Assert.Equal(0.5, state["web"]["automate_cpu_usage_cores"].Average);
        Assert.Equal(restored.AddSeconds(5), state["web"]["automate_memory_used_bytes"].Timestamp);
    }

    /// <summary>
    ///     Older recovery data cannot replace a newer RU observation or fabricate Cosmos CPU/memory.
    /// </summary>
    [Fact]
    public void Typed_metric_merge_rejects_old_samples_and_retains_native_ru()
    {
        var page = new ProjectDetails();
        var apply = typeof(ProjectDetails).GetMethod("ApplyTypedMetrics",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = DateTimeOffset.Parse("2026-10-10T11:00:00Z");
        apply.Invoke(page,
        [
            "cosmos",
            new[]
            {
                new DeploymentMetricPoint("cosmos", "automate_database_ru_usage_percent", "percent", now, 42, 42, 42)
            }
        ]);
        apply.Invoke(page,
        [
            "cosmos",
            new[]
            {
                new DeploymentMetricPoint("cosmos", "automate_database_ru_usage_percent", "percent", now.AddMinutes(-1),
                    3, 3, 3)
            }
        ]);
        var state = (Dictionary<string, Dictionary<string, DeploymentMetricPoint>>)typeof(ProjectDetails)
            .GetField("_typedMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var point = Assert.Single(state["cosmos"]).Value;
        Assert.Equal(42, point.Average);
        Assert.Equal(now, point.Timestamp);
    }

    /// <summary>
    ///     A reconnect callback after disposal must not touch a disposed cancellation source or service
    ///     provider.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Disposed_page_does_not_start_metric_recovery()
    {
        var page = new ProjectDetails();
        var deployment = new Deployment { Status = DeploymentStatus.Running };
        Field(page, "_app", new Domain.Entities.Application
        {
            Name = "fixture",
            SourcePathOrUrl = "fixture",
            SourceType = SourceType.Local,
            CsProjects = [new CsProject { Deployments = [deployment] }]
        });
        Field(page, "_terminalDeploymentId", (Guid?)deployment.Id);
        Field(page, "_analysisDisposed", true);
        Cancellation(page).Dispose();
        await RecoverAsync(page);
    }

    /// <summary>
    ///     Late provider replies and disposal exceptions are fenced before renderer access.
    /// </summary>
    /// <param name="failure">Failure flag or failure mode injected by the fixture.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_metric_recovery_ignores_page_disposal(bool failure)
    {
        var history = DispatchProxy.Create<IDeploymentHistoryService, HistoryProxy>();
        var proxy = (HistoryProxy)history;
        await using var services = new ServiceCollection().AddSingleton(history).BuildServiceProvider();
        var logger = new RecordingLogger();
        var page = new ProjectDetails();
        var deployment = new Deployment { Status = DeploymentStatus.Running };
        Field(page, "_app", new Domain.Entities.Application
        {
            Name = "fixture",
            SourcePathOrUrl = "fixture",
            SourceType = SourceType.Local,
            CsProjects = [new CsProject { Deployments = [deployment] }]
        });
        Field(page, "_terminalDeploymentId", (Guid?)deployment.Id);
        Property(page, "ScopeFactory", services.GetRequiredService<IServiceScopeFactory>());
        Property(page, "Logger", logger);
        var pending = RecoverAsync(page);
        await proxy.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Field(page, "_analysisDisposed", true);
        await Cancellation(page).CancelAsync();
        Cancellation(page).Dispose();
        if (failure) proxy.Result.SetException(new ObjectDisposedException("closed provider"));
        else proxy.Result.SetResult(new DeploymentMetricHistory([]));
        await pending;
        Assert.Empty(logger.Exceptions);
        Assert.Equal(1, proxy.Reads);
    }

    /// <summary>
    ///     Invokes only metric recovery, without OAuth, JavaScript, hosted workers or provider calls.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
    private static Task RecoverAsync(ProjectDetails page)
    {
        return (Task)typeof(ProjectDetails).GetMethod("RestoreMetricSnapshotAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, null)!;
    }

    /// <summary>
    ///     Arranges the real component's private lifecycle state.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <param name="value">Optional fixture member or event value.</param>
    private static void Field(ProjectDetails page, string name, object? value)
    {
        typeof(ProjectDetails).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    }

    /// <summary>
    ///     Supplies the component's ordinary injected port.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <param name="value">Fixture member value to assign.</param>
    private static void Property(ProjectDetails page, string name, object value)
    {
        typeof(ProjectDetails).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    }

    /// <summary>
    ///     Gets the page lifetime cancellation source for a deterministic disposal race.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <returns>The configured cancellation token source instance or response for this scenario.</returns>
    private static CancellationTokenSource Cancellation(ProjectDetails page)
    {
        return (CancellationTokenSource)typeof(ProjectDetails).GetField("_cloudPollCancellation",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!;
    }

    /// <summary>
    ///     Holds a provider reply independently of cancellation to model an already dispatched request.
    /// </summary>
    public class HistoryProxy : DispatchProxy
    {
        /// <summary>Signal or channel recording controlled operation starts.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completion source controlling the saved-metric response.</summary>
        public TaskCompletionSource<DeploymentMetricHistory> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Number of recorded fixture reads.</summary>
        public int Reads { get; private set; }

        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="args">
        ///     Arguments for the selected fixture port or reflected component member; ignored by this
        ///     fixture.
        /// </param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IDeploymentHistoryService.ReadMetricsAsync)) throw new NotSupportedException();
            Reads++;
            Started.TrySetResult();
            return Result.Task;
        }
    }

    /// <summary>
    ///     Captures any unexpected lifecycle failure.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ProjectDetails>
    {
        /// <summary>Exception objects captured for fixture assertions.</summary>
        public List<Exception?> Exceptions { get; } = [];

        /// <summary>
        ///     Returns no scope handle because this fixture does not retain scopes.
        /// </summary>
        /// <param name="state">Synthetic readiness or structured logger state; ignored by this fixture.</param>
        /// <typeparam name="TState">Type of structured logger state.</typeparam>
        /// <returns>Null; this fixture retains no scopes.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        /// <summary>
        ///     Enables every log level so the fixture can observe all emitted records.
        /// </summary>
        /// <param name="level">Reviewed log severity used to select safe output; ignored by this fixture.</param>
        /// <returns>True for every call in this fixture.</returns>
        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        /// <summary>
        ///     Captures or checks the logger call using the fixture assertions and supplied formatter.
        /// </summary>
        /// <param name="level">Reviewed log severity used to select safe output; ignored by this fixture.</param>
        /// <param name="id">Selected project identity; ignored by this fixture.</param>
        /// <param name="state">Synthetic readiness or structured logger state; ignored by this fixture.</param>
        /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
        /// <param name="formatter">
        ///     Provider log formatter; untrusted rendered text is not exported; ignored by this fixture.
        /// </param>
        /// <typeparam name="TState">Type of structured logger state.</typeparam>
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exceptions.Add(exception);
        }
    }
}
