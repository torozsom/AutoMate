using Microsoft.Extensions.DependencyInjection;
using Web.Services;

namespace Test.Web;

/// <summary>
///     Verifies independent operation lifetimes without relying on component private members.
/// </summary>
public sealed class ScopedApplicationOperationsTests
{
    /// <summary>
    ///     Overlapping operations receive different scoped dependencies and await disposal before returning.
    /// </summary>
    /// <returns>Completes when both overlapping scopes have been disposed.</returns>
    [Fact]
    public async Task Overlapping_operations_own_independent_scopes()
    {
        await using var provider = new ServiceCollection().AddScoped<Probe>().BuildServiceProvider();
        var operations = new ScopedApplicationOperations(provider.GetRequiredService<IServiceScopeFactory>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = new List<Probe>();
#pragma warning disable CS1587 // C# cannot emit local-function XML documentation.
        /// <summary>
        ///     Starts a scope that stays active until the fixture releases both overlapping operations.
        /// </summary>
        /// <returns>The operation-owned probe after asynchronous scope disposal.</returns>
        Task<Probe> Start()
        {
            return operations.RunAsync<Probe, Probe>(async probe =>
            {
                probes.Add(probe);
                await release.Task;
                Assert.False(probe.Disposed);
                return probe;
            });
        }
#pragma warning restore CS1587
        var first = Start();
        var second = Start();
        Assert.Equal(2, probes.Count);
        Assert.NotSame(probes[0], probes[1]);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, probe => Assert.True(probe.Disposed));
    }

    /// <summary>
    ///     Failure and cancellation escape unchanged while the operation scope still disposes.
    /// </summary>
    /// <returns>Completes after failure and cancellation cleanup have been observed.</returns>
    [Fact]
    public async Task Failure_and_cancellation_preserve_exceptions_and_dispose_dependencies()
    {
        await using var provider = new ServiceCollection().AddScoped<Probe>().BuildServiceProvider();
        var operations = new ScopedApplicationOperations(provider.GetRequiredService<IServiceScopeFactory>());
        foreach (var exception in new Exception[]
                     { new InvalidOperationException("fixture"), new OperationCanceledException() })
        {
            Probe? captured = null;
            var actual = await Record.ExceptionAsync(() => operations.RunAsync<Probe, bool>(probe =>
            {
                captured = probe;
                return Task.FromException<bool>(exception);
            }));
            Assert.Same(exception, actual);
            Assert.True(captured!.Disposed);
        }
    }

    /// <summary>
    ///     Records asynchronous scope disposal for the operation-lifetime scenarios.
    /// </summary>
    public sealed class Probe : IAsyncDisposable
    {
        /// <summary>Whether the owning scope has completed asynchronous disposal.</summary>
        public bool Disposed { get; private set; }

        /// <summary>
        ///     Records that the scope released this dependency.
        /// </summary>
        /// <returns>A completed disposal operation.</returns>
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
