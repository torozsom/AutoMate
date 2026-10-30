using Microsoft.Extensions.DependencyInjection;
using Web.Extensions;
using Web.Routes;
using Web.Routes.Endpoints;
using Web.Routes.Endpoints.Auth;

namespace Test.Web.Hosting;

/// <summary>Protects reflection discovery, transient lifetimes and static-before-fallback mapping order.</summary>
public sealed class EndpointDiscoveryTests
{
    /// <summary>Every maintained endpoint remains discoverable once and is resolved as an independent transient.</summary>
    [Fact]
    public async Task Endpoint_discovery_preserves_the_complete_ordered_registry()
    {
        IEndpoint[] expected =
        [
            new StaticAssetsEndpoint(), new AzureLoginEndpoint(), new GitHubLoginEndpoint(),
            new LoginEndpoint(), new LogoutEndpoint(), new DeploymentAnalysisEndpoint(),
            new DeploymentHistoryEndpoint(), new GitHubAppWebhookEndpoint(), new RazorComponentsEndpoint()
        ];
        var registrations = new ServiceCollection();
        Assert.Same(registrations, registrations.AddEndpoints());
        registrations.AddEndpoints();
        await using var services = registrations.BuildServiceProvider();
        var first = services.GetServices<IEndpoint>().ToArray();
        var second = services.GetServices<IEndpoint>().ToArray();
        Assert.Equal(expected.Select(endpoint => endpoint.GetType()), first.Select(endpoint => endpoint.GetType()));
        Assert.Equal(expected.Length, second.Length);
        for (var index = 0; index < first.Length; index++) Assert.NotSame(first[index], second[index]);
    }
}
