using System.Reflection;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Web.Components.Pages;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies that pending metric reads cannot update a closed page or report expected disposal as failure.</summary>
public sealed class ProjectDetailsMetricRecoveryTests
{
    /// <summary>A reconnect callback after disposal must not touch a disposed cancellation source or service provider.</summary>
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

    /// <summary>Late provider replies and disposal exceptions are fenced before renderer access.</summary>
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

    /// <summary>Invokes only metric recovery, without OAuth, JavaScript, hosted workers or provider calls.</summary>
    private static Task RecoverAsync(ProjectDetails page)
    {
        return (Task)typeof(ProjectDetails).GetMethod("RestoreMetricSnapshotAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, null)!;
    }

    /// <summary>Arranges the real component's private lifecycle state.</summary>
    private static void Field(ProjectDetails page, string name, object? value)
    {
        typeof(ProjectDetails).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    }

    /// <summary>Supplies the component's ordinary injected port.</summary>
    private static void Property(ProjectDetails page, string name, object value)
    {
        typeof(ProjectDetails).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    }

    /// <summary>Gets the page lifetime cancellation source for a deterministic disposal race.</summary>
    private static CancellationTokenSource Cancellation(ProjectDetails page)
    {
        return (CancellationTokenSource)typeof(ProjectDetails).GetField("_cloudPollCancellation",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!;
    }

    /// <summary>Holds a provider reply independently of cancellation to model an already dispatched request.</summary>
    public class HistoryProxy : DispatchProxy
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<DeploymentMetricHistory> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IDeploymentHistoryService.ReadMetricsAsync)) throw new NotSupportedException();
            Reads++;
            Started.TrySetResult();
            return Result.Task;
        }
    }

    /// <summary>Captures any unexpected lifecycle failure.</summary>
    private sealed class RecordingLogger : ILogger<ProjectDetails>
    {
        public List<Exception?> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exceptions.Add(exception);
        }
    }
}