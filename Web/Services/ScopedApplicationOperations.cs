namespace Web.Services;

/// <summary>
///     Executes independent application operations with fresh scoped dependencies instead of sharing circuit state.
/// </summary>
/// <param name="scopeFactory">Creates the operation scope; the circuit never owns its scoped dependencies.</param>
public sealed class ScopedApplicationOperations(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    ///     Resolves the requested application boundary and disposes its scope after asynchronous completion or failure.
    /// </summary>
    /// <typeparam name="TService">The application contract required by the operation.</typeparam>
    /// <typeparam name="TResult">The detached value returned to the caller.</typeparam>
    /// <param name="operation">Uses the scoped service without retaining it beyond completion.</param>
    /// <returns>The operation result; operation exceptions and cancellation propagate unchanged.</returns>
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> operation)
        where TService : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
