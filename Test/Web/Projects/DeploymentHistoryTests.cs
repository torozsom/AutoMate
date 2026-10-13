using System.Reflection;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Web.Components.Pages;
using Web.Components.Shared;

namespace Test.Web;

/// <summary>
///     Checks history loading independently of provider availability and browser layout.
/// </summary>
/// <exception cref="UnauthorizedAccessException">The returned task faults when the fixture failure is selected.</exception>
/// <exception cref="HttpRequestException">The returned task faults when the fixture failure is selected.</exception>
public sealed class DeploymentHistoryTests
{
    /// <summary>
    ///     A failing metric provider must not leave successfully fetched terminal logs blank.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     An empty retained page displays a visible notice instead of a blank terminal.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Empty_saved_page_displays_explicit_notice()
    {
        var (page, js) = CreatePage(new HistoryStub(new DeploymentTerminalHistory([], false, "Expired")));
        await LoadAsync(page);
        Assert.Equal("[No saved output in this terminal page.]\r\n", js.Output);
    }

    /// <summary>
    ///     Creates the component with a recording terminal and credential-free history service.
    /// </summary>
    /// <param name="history">Controlled deployment history port.</param>
    /// <returns>The grouped synthetic identities or fixture observations.</returns>
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

    /// <summary>
    ///     Exercises the page's loading action without attaching a browser renderer.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
    private static Task LoadAsync(DeploymentHistory page)
    {
        return (Task)typeof(DeploymentHistory).GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [null])!;
    }

    /// <summary>
    ///     Exercises the independent metric range action.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
    private static Task LoadMetricsAsync(DeploymentHistory page)
    {
        return (Task)typeof(DeploymentHistory).GetMethod("LoadMetricsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
    }

    /// <summary>
    ///     Changing metric ranges must not replace the log cursor, channel, or rendered output.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     An obsolete response cannot replace the latest range, even if a provider ignores
    ///     cancellation.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Metric authorization failures must hide the entire authorized history region.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Supplies component injection properties without creating a web host.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <param name="value">Fixture member value to assign.</param>
    private static void SetProperty(object target, string name, object value)
    {
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    /// <summary>
    ///     Supplies component references and readiness normally provided by Blazor.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <param name="value">Fixture member value to assign.</param>
    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    /// <summary>
    ///     Inspects the user-facing result of the loading action.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <typeparam name="T">Interface or result type created by the fixture.</typeparam>
    /// <returns>The configured t instance or response for this scenario.</returns>
    private static T GetField<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    }

    /// <summary>
    ///     Records the terminal interop calls to verify which channel is actually displayed.
    /// </summary>
    private sealed class RecordingJs : IJSRuntime
    {
        /// <summary>Current visible terminal output.</summary>
        public string Output { get; private set; } = "";

        /// <summary>
        ///     Delegates to the fixture interop overload using the same identifier and arguments.
        /// </summary>
        /// <param name="identifier">Provider or user identity to resolve.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <typeparam name="TValue">Type of the requested interop result.</typeparam>
        /// <returns>A value task returning the configured fixture reference or default requested value.</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        }

        /// <summary>
        ///     Records terminal reset/write interactions and returns the default interop result.
        /// </summary>
        /// <param name="identifier">Provider or user identity to resolve.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <typeparam name="TValue">Type of the requested interop result.</typeparam>
        /// <returns>A value task returning the configured fixture reference or default requested value.</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "xtermWrapper.clear") Output = "";
            if (identifier == "xtermWrapper.write") Output += (string)args![1]!;
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    /// <summary>
    ///     Returns saved logs while simulating an unavailable independent metric backend.
    /// </summary>
    /// <param name="logs">Saved terminal output supplied by the fixture.</param>
    /// <exception cref="HttpRequestException">The returned task faults when the fixture failure is selected.</exception>
    private sealed class HistoryStub(DeploymentTerminalHistory logs) : IDeploymentHistoryService
    {
        /// <summary>Counts actual log queries independently of metric requests.</summary>
        public int LogReads { get; private set; }

        /// <summary>Controllable independent provider response for concurrency and authorization scenarios.</summary>
        public Func<Task<DeploymentMetricHistory>> Metrics { get; set; } =
            () => Task.FromException<DeploymentMetricHistory>(new HttpRequestException("Metric backend unavailable"));

        /// <summary>
        ///     Supplies configured saved terminal history and records any fixture read count.
        /// </summary>
        /// <param name="userId">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="cursor">Opaque history position, or null to read the latest page; ignored by this fixture.</param>
        /// <param name="backwards">
        ///     Whether to read earlier records rather than records after the cursor; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="limit">Requested count or configured admission bound; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment terminal history instance or response for this scenario.</returns>
        public Task<DeploymentTerminalHistory> ReadLogsAsync(Guid userId, Guid projectId, Guid deploymentId,
            long cursor, bool backwards, int limit, CancellationToken cancellationToken = default)
        {
            LogReads++;
            return Task.FromResult(logs);
        }

        /// <summary>
        ///     Supplies configured saved metric history for component assertions.
        /// </summary>
        /// <param name="userId">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="start">UTC observation-window start; ignored by this fixture.</param>
        /// <param name="end">UTC observation-window end; ignored by this fixture.</param>
        /// <param name="maximumPoints">The maximum number of returned aggregate points; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured deployment metric history instance or response for this scenario.</returns>
        public Task<DeploymentMetricHistory> ReadMetricsAsync(Guid userId, Guid projectId, Guid deploymentId,
            DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken = default)
        {
            return Metrics();
        }

        /// <summary>
        ///     Rejects get preferences because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="userId">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        public Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid userId, Guid projectId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///     Rejects set managed consent because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="userId">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="enabled">Whether to include the option; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        public Task SetManagedConsentAsync(Guid userId, Guid projectId, bool enabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///     Rejects set runtime collection because this member is deliberately unavailable in the
        ///     fixture.
        /// </summary>
        /// <param name="userId">Synthetic owner identity; ignored by this fixture.</param>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="enabled">Whether to include the option; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotSupportedException">The selected fixture member or operation is deliberately unsupported.</exception>
        public Task SetRuntimeCollectionAsync(Guid userId, Guid projectId, bool enabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
