using System.Reflection;
using Application.Abstractions.Ai;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies polling serialization through the shared current/history analysis presenter.</summary>
public sealed class DeploymentAnalysisPollingTests
{
    /// <summary>Concurrent refreshes do not overlap; failures retain saved results and the next refresh recovers.</summary>
    [Fact]
    public async Task Refresh_serializes_reads_preserves_result_on_failure_and_recovers()
    {
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var port = DispatchProxy.Create<IDeploymentAnalysisService, Port>();
        var probe = (Port)port;
        var pending =
            new TaskCompletionSource<DeploymentAnalysisView?>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Read = pending.Task;
        await using var services = new ServiceCollection().AddSingleton(port).BuildServiceProvider();
        var component = new DeploymentAnalysisSection();
        Set(component, "OwnerId", owner);
        Set(component, "ProjectId", project);
        Set(component, "DeploymentId", deployment);
        Set(component, "Scopes", services.GetRequiredService<IServiceScopeFactory>());
        Set(component, "Logger", NullLogger<DeploymentAnalysisSection>.Instance);
        Set(component, "_identity", (owner, project, (Guid?)deployment));
        var saved = View(deployment, AiAnalysisStatus.Running);
        Set(component, "_latest", saved);
        var read = Refresh(component);
        await Refresh(component);
        Assert.Equal(1, probe.Reads);
        pending.SetException(new IOException("private-provider-payload"));
        await read;
        Assert.Same(saved, Get(component, "_latest"));
        Assert.DoesNotContain("private", (string)Get(component, "_message")!);
        var completed = View(deployment, AiAnalysisStatus.Completed);
        probe.Read = Task.FromResult<DeploymentAnalysisView?>(completed);
        await Refresh(component);
        Assert.Same(completed, Get(component, "_latest"));
        await component.DisposeAsync();
    }

    /// <summary>Creates minimal persisted metadata without contacting a provider.</summary>
    private static DeploymentAnalysisView View(Guid deployment, AiAnalysisStatus status)
    {
        return new DeploymentAnalysisView(Guid.NewGuid(),
            deployment, status, AiAnalysisTrigger.Manual, null, [], [], null, DateTimeOffset.UtcNow, null);
    }

    /// <summary>Invokes the shared refresh used by its polling loop.</summary>
    private static Task Refresh(DeploymentAnalysisSection component)
    {
        return (Task)typeof(DeploymentAnalysisSection)
            .GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null)!;
    }

    /// <summary>Arranges component ports and saved selection state.</summary>
    private static void Set(object component, string name, object value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var property = component.GetType().GetProperty(name, flags);
        if (property is not null) property.SetValue(component, value);
        else component.GetType().GetField(name, flags)!.SetValue(component, value);
    }

    /// <summary>Reads the persisted view independently of rendering.</summary>
    private static object? Get(object component, string name)
    {
        return component.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component);
    }

    /// <summary>Controlled catalog and result queries; no analysis submission is available.</summary>
    public class Port : DispatchProxy
    {
        /// <summary>Deferred result used to exercise overlapping reads.</summary>
        public Task<DeploymentAnalysisView?> Read { get; set; } = Task.FromResult<DeploymentAnalysisView?>(null);

        /// <summary>Number of reads reaching the application boundary.</summary>
        public int Reads { get; private set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "GetLatestAsync")
            {
                Reads++;
                return Read;
            }

            return method?.Name switch
            {
                "GetPreferencesAsync" => Task.FromResult<AssessmentPreferences?>(null),
                "ListAsync" => Task.FromResult<IReadOnlyList<DeploymentAnalysisView>>([]),
                _ => throw new NotSupportedException()
            };
        }
    }
}