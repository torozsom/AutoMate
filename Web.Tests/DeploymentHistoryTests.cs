using System.Reflection;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Web.Components.Pages;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Checks history loading independently of provider availability and browser layout.</summary>
public sealed class DeploymentHistoryTests
{
    /// <summary>A failing metric provider must not leave successfully fetched terminal logs blank.</summary>
    [Fact]
    public async Task Metric_failure_still_renders_selected_saved_channel()
    {
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var history = new HistoryStub(new DeploymentTerminalHistory([
            new DeploymentTerminalLog(1, project, deployment, "web", "web started\r\n"),
            new DeploymentTerminalLog(2, project, deployment, "db", "db started\r\n"),
            new DeploymentTerminalLog(3, project, deployment, "web", "request served\r\n")
        ], false));
        var (page, js) = CreatePage(history);
        SetProperty(page, "ProjectId", project);
        SetProperty(page, "DeploymentId", deployment);

        await LoadAsync(page);
        await LoadMetricsAsync(page);

        Assert.Equal("web started\r\nrequest served\r\n", js.Output);
        Assert.Equal("web", GetField<string>(page, "_channel"));
        Assert.True(GetField<bool>(page, "_authorized"));
        Assert.Contains("temporarily unavailable", GetField<string>(page, "_metricAvailability"));
        Assert.Null(GetField<string?>(page, "_error"));
    }

    /// <summary>An empty retained page displays a visible notice instead of a blank terminal.</summary>
    [Fact]
    public async Task Empty_saved_page_displays_explicit_notice()
    {
        var (page, js) = CreatePage(new HistoryStub(new DeploymentTerminalHistory([], false, "Expired")));
        await LoadAsync(page);
        Assert.Equal("[No saved output in this terminal page.]\r\n", js.Output);
    }

    /// <summary>Creates the component with a recording terminal and credential-free history service.</summary>
    private static (DeploymentHistory Page, RecordingJs Js) CreatePage(IDeploymentHistoryService history)
    {
        var js = new RecordingJs();
        var terminal = new Terminal();
        SetProperty(terminal, "JSRuntime", js);
        SetField(terminal, "_isReady", true);
        var page = new DeploymentHistory();
        var services = new ServiceCollection().AddSingleton(history).BuildServiceProvider();
        SetProperty(page, "ServiceScopes", services.GetRequiredService<IServiceScopeFactory>());
        SetProperty(page, "Logger", NullLogger<DeploymentHistory>.Instance);
        SetField(page, "_terminal", terminal);
        return (page, js);
    }

    /// <summary>Exercises the page's loading action without attaching a browser renderer.</summary>
    private static Task LoadAsync(DeploymentHistory page)
    {
        return (Task)typeof(DeploymentHistory).GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [null])!;
    }

    /// <summary>Exercises the independent metric range action.</summary>
    private static Task LoadMetricsAsync(DeploymentHistory page)
    {
        return (Task)typeof(DeploymentHistory).GetMethod("LoadMetricsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
    }

    /// <summary>Changing metric ranges must not replace the log cursor, channel, or rendered output.</summary>
    [Fact]
    public async Task Metric_range_changes_preserve_terminal_and_log_paging()
    {
        var history = new HistoryStub(new DeploymentTerminalHistory([
            new DeploymentTerminalLog(1, Guid.NewGuid(), Guid.NewGuid(), "build", "saved output")
        ], false));
        var (page, js) = CreatePage(history);
        await LoadAsync(page);
        SetField(page, "_earlierCursor", "unchanged cursor");
        SetField(page, "_metricDays", 7);
        await LoadMetricsAsync(page);
        Assert.Equal("saved output", js.Output);
        Assert.Equal("build", GetField<string>(page, "_channel"));
        Assert.Equal("unchanged cursor", GetField<string>(page, "_earlierCursor"));
        Assert.Equal(1, history.LogReads);
    }

    /// <summary>An obsolete response cannot replace the latest range, even if a provider ignores cancellation.</summary>
    [Fact]
    public async Task Latest_metric_range_wins_and_defaults_to_a_measured_container()
    {
        var older = new TaskCompletionSource<DeploymentMetricHistory>();
        var history = new HistoryStub(new DeploymentTerminalHistory([], false));
        var calls = 0;
        var now = DateTimeOffset.UtcNow;
        history.Metrics = () => ++calls == 1
            ? older.Task
            : Task.FromResult(new DeploymentMetricHistory([
                new DeploymentMetricPoint("web", TelemetryPresentation.Cpu, "cores", now, .006, 0, .012),
                new DeploymentMetricPoint("db", TelemetryPresentation.Memory, "bytes", now, 1024, 1024, 1024)
            ]));
        var (page, _) = CreatePage(history);
        var first = LoadMetricsAsync(page);
        SetField(page, "_metricDays", 7);
        await LoadMetricsAsync(page);
        older.SetResult(new DeploymentMetricHistory([
            new DeploymentMetricPoint("old", TelemetryPresentation.Cpu, "cores", now, 99, 99, 99)
        ]));
        await first;
        Assert.Equal(7, GetField<int>(page, "_loadedMetricDays"));
        Assert.Equal("db", GetField<string>(page, "_metricContainer"));
        Assert.Equal(2, GetField<IReadOnlyList<DeploymentMetricPoint>>(page, "_metrics").Count);
        Assert.False(GetField<bool>(page, "_metricBusy"));
    }

    /// <summary>Metric authorization failures must hide the entire authorized history region.</summary>
    [Fact]
    public async Task Metric_access_denied_hides_history()
    {
        var history = new HistoryStub(new DeploymentTerminalHistory([], false))
        {
            Metrics = () => Task.FromException<DeploymentMetricHistory>(new UnauthorizedAccessException())
        };
        var (page, _) = CreatePage(history);
        await LoadAsync(page);
        Assert.True(GetField<bool>(page, "_authorized"));
        await LoadMetricsAsync(page);
        Assert.False(GetField<bool>(page, "_authorized"));
        Assert.Equal("Deployment history access denied.", GetField<string>(page, "_error"));
    }

    /// <summary>Supplies component injection properties without creating a web host.</summary>
    private static void SetProperty(object target, string name, object value)
    {
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    /// <summary>Supplies component references and readiness normally provided by Blazor.</summary>
    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    /// <summary>Inspects the user-facing result of the loading action.</summary>
    private static T GetField<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    }

    /// <summary>Records the terminal interop calls to verify which channel is actually displayed.</summary>
    private sealed class RecordingJs : IJSRuntime
    {
        /// <summary>Current visible terminal output.</summary>
        public string Output { get; private set; } = "";

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        }

        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "xtermWrapper.clear") Output = "";
            if (identifier == "xtermWrapper.write") Output += (string)args![1]!;
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    /// <summary>Returns saved logs while simulating an unavailable independent metric backend.</summary>
    private sealed class HistoryStub(DeploymentTerminalHistory logs) : IDeploymentHistoryService
    {
        /// <summary>Counts actual log queries independently of metric requests.</summary>
        public int LogReads { get; private set; }

        /// <summary>Controllable independent provider response for concurrency and authorization scenarios.</summary>
        public Func<Task<DeploymentMetricHistory>> Metrics { get; set; } =
            () => Task.FromException<DeploymentMetricHistory>(new HttpRequestException("Metric backend unavailable"));

        /// <inheritdoc />
        public Task<DeploymentTerminalHistory> ReadLogsAsync(Guid userId, Guid projectId, Guid deploymentId,
            long cursor, bool backwards, int limit, CancellationToken cancellationToken = default)
        {
            LogReads++;
            return Task.FromResult(logs);
        }

        /// <inheritdoc />
        public Task<DeploymentMetricHistory> ReadMetricsAsync(Guid userId, Guid projectId, Guid deploymentId,
            DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken = default)
        {
            return Metrics();
        }

        /// <inheritdoc />
        public Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid userId, Guid projectId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task SetManagedConsentAsync(Guid userId, Guid projectId, bool enabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task SetRuntimeCollectionAsync(Guid userId, Guid projectId, bool enabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}