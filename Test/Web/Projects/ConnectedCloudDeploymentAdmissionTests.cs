using System.Reflection;
using System.Security.Claims;
using Application.Data.Users;
using Domain.DTO.Azure;
using Domain.DTO.Deployment;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Test.Web.Diagnostics;
using Web.Services;

namespace Test.Web.Projects;

/// <summary>
///     Characterizes the two deployment pages' connection ordering and owner-fence differences at the new boundary.
/// </summary>
public sealed class ConnectedCloudDeploymentAdmissionTests
{
    /// <summary>
    ///     Dashboard stops after missing Azure; Project Details retains its combined-connection lookup behavior.
    /// </summary>
    /// <param name="ownerFence">Whether to use Dashboard's existing compatibility policy.</param>
    /// <param name="hasAzure">Whether the synthetic Azure connection exists.</param>
    /// <param name="hasGitHub">Whether the synthetic GitHub token exists.</param>
    /// <param name="expected">The existing connection availability.</param>
    /// <returns>Completes after recording the connection lookup sequence.</returns>
    [Theory]
    [InlineData(true, false, true, CloudConnectionState.MissingAzure)]
    [InlineData(false, false, true, CloudConnectionState.MissingAzure)]
    [InlineData(true, true, false, CloudConnectionState.MissingGitHub)]
    [InlineData(false, true, false, CloudConnectionState.MissingGitHub)]
    [InlineData(true, true, true, CloudConnectionState.Ready)]
    [InlineData(false, true, true, CloudConnectionState.Ready)]
    public async Task Connection_order_and_page_specific_early_exit_remain_compatible(bool ownerFence,
        bool hasAzure, bool hasGitHub, CloudConnectionState expected)
    {
        var owner = Guid.NewGuid();
        var calls = new List<string>();
        var users = DispatchProxy.Create<IUserService, OperationalLoggingTests.PortProxy>();

        (Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(users)).Call = (method, args) =>
        {
            calls.Add(method!.Name);
            if (method.Name == nameof(IUserService.GetAzureCloudCredentialsAsync))
            {
                Assert.Equal(owner, args![0]);

                return Task.FromResult(hasAzure ? new AzureCloudCredentialsDto() : null);
            }

            Assert.Equal(nameof(IUserService.GetUserDetailsFromIdentifierAsync), method.Name);
            Assert.Equal(owner.ToString(), args![0]);

            return Task.FromResult((owner, hasGitHub ? "synthetic-token" : null, false));
        };
        await using var provider = new ServiceCollection().AddSingleton(users).BuildServiceProvider();
        var operations = new ScopedApplicationOperations(provider.GetRequiredService<IServiceScopeFactory>());
        var preparation = await new ConnectedCloudDeploymentAdmission(operations)
            .PrepareAsync(owner, new Authentication(owner), ownerFence, () => true);

        Assert.Equal(expected, preparation.State);
        Assert.Equal(nameof(IUserService.GetAzureCloudCredentialsAsync), calls[0]);
        Assert.Equal(ownerFence && !hasAzure ? 1 : 2, calls.Count);

        if (expected != CloudConnectionState.Ready)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                preparation.StartAsync(new DeploymentConfigDto(), "owner", "repo", "app", null));
    }

    /// <summary>
    ///     A disposed/account-switched Dashboard never performs the second lookup or admits work.
    /// </summary>
    /// <returns>Completes after observing the captured owner fence.</returns>
    [Fact]
    public async Task Obsolete_dashboard_operation_stops_before_github_lookup()
    {
        var users = DispatchProxy.Create<IUserService, OperationalLoggingTests.PortProxy>();

        (Assert.IsAssignableFrom<OperationalLoggingTests.PortProxy>(users)).Call = (method, _) =>
        {
            Assert.Equal(nameof(IUserService.GetAzureCloudCredentialsAsync), method!.Name);

            return Task.FromResult<AzureCloudCredentialsDto?>(new AzureCloudCredentialsDto());
        };
        await using var provider = new ServiceCollection().AddSingleton(users).BuildServiceProvider();
        var operations = new ScopedApplicationOperations(provider.GetRequiredService<IServiceScopeFactory>());
        var owner = Guid.NewGuid();
        var preparation = await new ConnectedCloudDeploymentAdmission(operations)
            .PrepareAsync(owner, new Authentication(owner), true, () => false);

        Assert.Equal(CloudConnectionState.Obsolete, preparation.State);
    }

    /// <summary>
    ///     Supplies the originating circuit's synthetic owner claim without OAuth traffic.
    /// </summary>
    /// <param name="owner">The scenario owner.</param>
    private sealed class Authentication(Guid owner) : AuthenticationStateProvider
    {
        /// <summary>
        ///     Returns the same owner claim used by the existing page resolver.
        /// </summary>
        /// <returns>The synthetic authenticated circuit state.</returns>
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(
                new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, owner.ToString())], "fixture"))));
        }
    }
}
