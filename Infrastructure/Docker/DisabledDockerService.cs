using Application.Abstractions.Docker;

namespace Infrastructure.Docker;

/// <summary>
///     Safe placeholder used by hosted SaaS instances. It prevents any Docker daemon interaction
///     while keeping pages that display historical local deployments constructible.
/// </summary>
public sealed class DisabledDockerService : IDockerService
{
    private static InvalidOperationException Disabled() =>
        new("The Docker daemon is not available in this AutoMate hosting profile.");

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> BuildImageAsync(string sourcePath, string imageTag, CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(Disabled());
    public Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort, int containerPort = 8080,
        string? envVarsJson = null, CancellationToken cancellationToken = default) => Task.FromException<string?>(Disabled());
    public Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default) => Task.FromException<bool>(Disabled());
    public Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default) => Task.FromException<bool>(Disabled());
    public Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<string>());
    public Task StreamContainerLogsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken) => Task.FromException(Disabled());
    public Task StreamContainerMetricsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken) => Task.FromException(Disabled());
    public Task<int> GetContainerHostPortAsync(string containerName, CancellationToken cancellationToken = default) =>
        Task.FromException<int>(Disabled());
}
