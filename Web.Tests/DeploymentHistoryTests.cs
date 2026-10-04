using System.Reflection;
using Application.Abstractions.Diagnostics;
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
        SetProperty(page, "History", history);
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
        /// <inheritdoc />
        public Task<DeploymentTerminalHistory> ReadLogsAsync(Guid userId, Guid projectId, Guid deploymentId,
            long cursor, bool backwards, int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(logs);
        }

        /// <inheritdoc />
        public Task<DeploymentMetricHistory> ReadMetricsAsync(Guid userId, Guid projectId, Guid deploymentId,
            DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken = default)
        {
            return Task.FromException<DeploymentMetricHistory>(new HttpRequestException("Metric backend unavailable"));
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