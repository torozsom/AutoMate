using System.Reflection;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Execution;
using Domain.Enums.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Components.Shared;

namespace Test.Web.Analysis;

/// <summary>
///     Verifies the shared presenter rather than the previous page-specific polling implementation.
/// </summary>
public sealed class DeploymentAnalysisSectionTests
{
    /// <summary>Private component state is inspected without creating authentication/provider connections.</summary>
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>
    ///     Route changes and disposal prevent an old deployment result from reaching either page.
    /// </summary>
    /// <param name="dispose">Whether to dispose the component while a request is pending.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_result_is_fenced_after_selection_change_or_disposal(bool dispose)
    {
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var service = DispatchProxy.Create<IDeploymentAnalysisService, Port>();
        var port = Assert.IsAssignableFrom<Port>(service);
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

    /// <summary>
    ///     Supplies injection or selection state to the actual shared component.
    /// </summary>
    /// <param name="component">Component instance accessed by the fixture.</param>
    /// <param name="member">Reflected member name accessed by the fixture.</param>
    /// <param name="value">Fixture member value to assign.</param>
    private static void Set(object component, string member, object value)
    {
        var property = component.GetType().GetProperty(member, Flags);

        if (property is not null) property.SetValue(component, value);
        else component.GetType().GetField(member, Flags)!.SetValue(component, value);
    }

    /// <summary>
    ///     Only deferred scoped reads are allowed; no provider request is implemented.
    /// </summary>
    public class Port : DispatchProxy
    {
        /// <summary>Controlled saved-analysis response.</summary>
        public Task<DeploymentAnalysisView?> Read { get; set; } = Task.FromResult<DeploymentAnalysisView?>(null);

        /// <summary>Ensures stale-response assertions actually reach the deferred read.</summary>
        public int ReadCalls { get; private set; }

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
        protected override object Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name switch
            {
                "GetPreferencesAsync" => Task.FromResult<AssessmentPreferences?>(null),
                "GetLatestAsync" => ReadLatest(),
                "ListAsync" => Task.FromResult<IReadOnlyList<DeploymentAnalysisView>>([]),
                _ => throw new NotSupportedException()
            };
        }

        /// <summary>
        ///     Records deferred result reads independently of catalog queries.
        /// </summary>
        /// <returns>
        ///     A task returning the configured deployment analysis view result, or null when the fixture
        ///     supplies no value.
        /// </returns>
        private Task<DeploymentAnalysisView?> ReadLatest()
        {
            ReadCalls++;

            return Read;
        }
    }
}
