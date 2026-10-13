using System.Net;
using Domain.DTO;
using Infrastructure.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Test.Infra.TestSupport;

namespace Test.Infra.Azure;

/// <summary>
///     Ensures diagnostics provider readiness precedes deployment and safe failures do not leak
///     response bodies.
/// </summary>
public sealed class AzureResourceProviderRegistrarTests
{
    /// <summary>
    ///     The Insights provider is registered and polled before setup returns.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Registers_missing_insights_and_waits_for_readiness()
    {
        var registered = false;
        var reads = 0;
        var registrar = new AzureResourceProviderRegistrar(new StubHttpClientFactory(
            new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("Microsoft.Insights"))
                {
                    if (request.Method == HttpMethod.Post) registered = true;
                    else reads++;
                    return DelegateHttpMessageHandler.Json("{\"registrationState\":\"" +
                                                           (registered ? "Registered" : "NotRegistered") + "\"}");
                }

                return DelegateHttpMessageHandler.Json("{\"registrationState\":\"Registered\"}");
            })), NullLogger.Instance);
        await registrar.EnsureRequiredProvidersAsync(
            new AzureCloudCredentialsDto { SubscriptionId = "sub", AccessToken = "token" }, new DeploymentConfigDto(),
            default);
        Assert.True(registered);
        Assert.Equal(2, reads);
    }

    /// <summary>
    ///     Subscription registration denial has fixed guidance without arbitrary provider details.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Denial_omits_provider_body_and_credentials()
    {
        var registrar = new AzureResourceProviderRegistrar(new StubHttpClientFactory(
                new DelegateHttpMessageHandler(request =>
                    request.Method == HttpMethod.Post
                        ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                            { Content = new StringContent("sensitive-provider-body") }
                        : DelegateHttpMessageHandler.Json("{\"registrationState\":\"NotRegistered\"}"))),
            NullLogger.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registrar.EnsureRequiredProvidersAsync(
            new AzureCloudCredentialsDto { SubscriptionId = "private-sub", AccessToken = "private-token" },
            new DeploymentConfigDto(), default));
        Assert.Contains("Register it manually", error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.DoesNotContain("private", error.Message);
    }

    /// <summary>
    ///     Cancellation bounds a provider stuck in Registering without dispatching a workflow.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Stalled_registration_observes_deadline()
    {
        var registrar = new AzureResourceProviderRegistrar(new StubHttpClientFactory(new DelegateHttpMessageHandler(_ =>
            DelegateHttpMessageHandler.Json("{\"registrationState\":\"Registering\"}"))), NullLogger.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registrar.EnsureRequiredProvidersAsync(
            new AzureCloudCredentialsDto { SubscriptionId = "sub", AccessToken = "token" }, new DeploymentConfigDto(),
            deadline.Token));
    }
}
