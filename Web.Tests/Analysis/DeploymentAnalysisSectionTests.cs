using System.Reflection;
using Application.Abstractions.Ai;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies the shared presenter rather than the previous page-specific polling implementation.</summary>
public sealed class DeploymentAnalysisSectionTests
{
    /// <summary>Private component state is inspected without creating authentication/provider connections.</summary>
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>Route changes and disposal prevent an old deployment result from reaching either page.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_result_is_fenced_after_selection_change_or_disposal(bool dispose)
    {
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var service = DispatchProxy.Create<IDeploymentAnalysisService, Port>();
        var port = (Port)service;
        var pending =
            new TaskCompletionSource<DeploymentAnalysisView?>(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Read = pending.Task;
        using var services = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
        var component = new DeploymentAnalysisSection();
        Set(component, "OwnerId", owner);
        Set(component, "ProjectId", project);
        Set(component, "DeploymentId", deployment);
        Set(component, "Scopes", services.GetRequiredService<IServiceScopeFactory>());
        Set(component, "Logger", NullLogger<DeploymentAnalysisSection>.Instance);
        Set(component, "_identity", (owner, project, (Guid?)deployment));
        var read = (Task)typeof(DeploymentAnalysisSection).GetMethod("RefreshAsync", Flags)!.Invoke(component, null)!;
        Assert.Equal(1, port.ReadCalls);
        if (dispose)
        {
            await component.DisposeAsync();
        }
        else
        {
            Set(component, "DeploymentId", Guid.NewGuid());
            Set(component, "_identity", (owner, project, component.DeploymentId));
        }

        pending.SetResult(new DeploymentAnalysisView(Guid.NewGuid(), deployment, AiAnalysisStatus.Completed,
            AiAnalysisTrigger.Manual, "old deployment guidance", [], [], null, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        await read;
        Assert.Null(typeof(DeploymentAnalysisSection).GetField("_latest", Flags)!.GetValue(component));
        if (!dispose) await component.DisposeAsync();
    }

    /// <summary>Supplies injection or selection state to the actual shared component.</summary>
    private static void Set(object component, string member, object value)
    {
        var property = component.GetType().GetProperty(member, Flags);
        if (property is not null) property.SetValue(component, value);
        else component.GetType().GetField(member, Flags)!.SetValue(component, value);
    }

    /// <summary>Only deferred scoped reads are allowed; no provider request is implemented.</summary>
    public class Port : DispatchProxy
    {
        public Task<DeploymentAnalysisView?> Read { get; set; } = Task.FromResult<DeploymentAnalysisView?>(null);

        /// <summary>Ensures stale-response assertions actually reach the deferred read.</summary>
        public int ReadCalls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name switch
            {
                "GetPreferencesAsync" => Task.FromResult<AssessmentPreferences?>(null),
                "GetLatestAsync" => ReadLatest(),
                "ListAsync" => Task.FromResult<IReadOnlyList<DeploymentAnalysisView>>([]),
                _ => throw new NotSupportedException()
            };
        }

        /// <summary>Records deferred result reads independently of catalog queries.</summary>
        private Task<DeploymentAnalysisView?> ReadLatest()
        {
            ReadCalls++;
            return Read;
        }
    }
}