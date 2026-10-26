using System.Reflection;
using Application.Abstractions.Hosting;
using Application.Orchestration.Cloud;
using Application.Orchestration.Jobs;
using Domain.DTO.Azure;
using Domain.DTO.Deployment;
using Domain.Enums.Deployments;

namespace Test.App.Orchestration;

/// <summary>
///     Verifies cloud admission requests at application ports rather than component implementation details.
/// </summary>
public sealed class CloudDeploymentAdmissionTests
{
    /// <summary>
    ///     Both profiles retain the same request fields and use exactly their existing admission port.
    /// </summary>
    /// <param name="localEnabled">Whether the SelfHosted queue is selected.</param>
    /// <returns>Completes after the selected admission port accepts the request.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Admission_preserves_profile_routing_and_payload(bool localEnabled)
    {
        var config = new DeploymentConfigDto { ProjectId = Guid.NewGuid(), ProjectName = "fixture" };
        var azure = new AzureCloudCredentialsDto { AccessToken = "synthetic-azure" };
        var request = new CloudDeploymentAdmissionRequest(Guid.NewGuid(), config, "owner", "repo", "app",
            "synthetic-github", azure, "stable-key");
        var receipt = new CloudDeploymentReceipt(Guid.NewGuid(), CloudRunPhase.Queued, DateTimeOffset.UtcNow, null);
        var calls = 0;
        var capabilities = Port<IDeploymentCapabilities>((_, _) => localEnabled);

        var queue = Port<IDeploymentJobQueue>((method, args) =>
        {
            Assert.True(localEnabled);
            Assert.Equal(nameof(IDeploymentJobQueue.EnqueueAsync), method!.Name);
            var job = Assert.IsType<CloudDeploymentJob>(args![0]);
            Assert.Same(config, job.Request.Config);
            Assert.Same(azure, job.Request.AzureCredentials);
            Assert.Equal(request.Owner, job.Request.RequestingUserId);
            Assert.Equal("synthetic-github", job.Request.GitHubAccessToken);
            Assert.Equal("synthetic-github", job.Request.GitHubContainerRegistryToken);
            Assert.Equal("owner", job.Request.RepositoryOwner);
            Assert.Equal("repo", job.Request.RepositoryName);
            Assert.Equal("app", job.Request.CsProjectName);
            Assert.Equal(".", job.Request.RepositoryRoot);
            Assert.Equal("net10.0", job.Request.Metadata.TargetFramework);
            Assert.Equal("10.0", job.Request.Metadata.DotNetVersion);
            Assert.True(job.Request.Metadata.IsWebProject);

            calls++;

            return ValueTask.CompletedTask;
        });

        var runs = Port<ICloudDeploymentRunService>((method, args) =>
        {
            Assert.False(localEnabled);
            Assert.Equal(nameof(ICloudDeploymentRunService.StartAsync), method!.Name);
            var start = Assert.IsType<CloudDeploymentStart>(args![0]);
            Assert.Same(config, start.Config);
            Assert.Equal(request.Owner, start.UserId);
            Assert.Equal(config.ProjectId, start.ProjectId);
            Assert.Equal("stable-key", start.IdempotencyKey);
            Assert.Equal("synthetic-github", start.UserGitHubAccessToken);
            Assert.Equal("owner", start.RepositoryOwner);
            Assert.Equal("repo", start.RepositoryName);
            Assert.Equal("app", start.CsProjectName);
            Assert.Equal(".", start.RepositoryRoot);
            Assert.Equal("net10.0", start.Metadata.TargetFramework);
            Assert.Equal("10.0", start.Metadata.DotNetVersion);
            Assert.True(start.Metadata.IsWebProject);

            calls++;

            return Task.FromResult(receipt);
        });

        var actual = await new CloudDeploymentAdmission(capabilities, queue, runs).StartAsync(request);

        Assert.Equal(1, calls);

        if (localEnabled) Assert.Null(actual);

        else Assert.Same(receipt, actual);
    }

    /// <summary>
    ///     Creates a recording application port with only the operations used by this scenario.
    /// </summary>
    /// <typeparam name="T">The substituted application contract.</typeparam>
    /// <param name="call">Validates and records calls at the contract boundary.</param>
    /// <returns>The recording port.</returns>
    private static T Port<T>(Func<MethodInfo?, object?[]?, object?> call) where T : class
    {
        var port = DispatchProxy.Create<T, PortProxy>();

        ((PortProxy)(object)port).Call = call;

        return port;
    }

    /// <summary>
    ///     Forwards application contract calls to the scenario's recording delegate.
    /// </summary>
    public class PortProxy : DispatchProxy
    {
        /// <summary>Gets or sets the delegate that validates each contract invocation.</summary>
        public Func<MethodInfo?, object?[]?, object?> Call { get; set; } = null!;

        /// <summary>
        ///     Invokes the scenario delegate without contacting application dependencies.
        /// </summary>
        /// <param name="method">The invoked contract method.</param>
        /// <param name="args">The supplied contract arguments.</param>
        /// <returns>The scenario's configured response.</returns>
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return Call(method, args);
        }
    }
}
