using System.Net;
using System.Text.Json;
using Application.Abstractions.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Web.Configs;
using Xunit;

namespace Web.Tests;

/// <summary>Exercises real health middleware, HTTP status codes, liveness isolation and finite readiness JSON.</summary>
public sealed class AnalysisReadinessTests
{
    /// <summary>Disabled AI remains healthy; enabled blocked/missing configuration and database failure are explicit.</summary>
    [Theory]
    [InlineData(AnalysisConfigurationState.Disabled, true, 200, "Healthy")]
    [InlineData(AnalysisConfigurationState.Ready, true, 200, "Healthy")]
    [InlineData(AnalysisConfigurationState.EgressDisabled, true, 503, "Degraded")]
    [InlineData(AnalysisConfigurationState.Unavailable, true, 503, "Degraded")]
    [InlineData(AnalysisConfigurationState.Invalid, true, 503, "Unhealthy")]
    [InlineData(AnalysisConfigurationState.Disabled, false, 503, "Unhealthy")]
    [InlineData(AnalysisConfigurationState.Ready, false, 503, "Unhealthy")]
    public async Task Readiness_maps_states_and_liveness_does_not_probe_queue(AnalysisConfigurationState state,
        bool available, int status, string health)
    {
        var port = new ReadinessPort { Snapshot = new DeploymentAnalysisReadiness(state, available) };
        await VerifyHttpAsync(port, status, health, state.ToString(), available ? "Available" : "Unavailable");
    }

    /// <summary>Unexpected failures and framework timeouts return safe JSON and pass cancellation to the running probe.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exceptions_and_timeouts_do_not_disclose_details(bool timeout)
    {
        var port = new ReadinessPort { Fail = !timeout, WaitForCancellation = timeout };
        await VerifyHttpAsync(port, 503, "Unhealthy", "Unavailable", "Unavailable");
        if (timeout) Assert.True(port.CancellationObserved);
    }

    /// <summary>Hosts the production health mapping with only a scoped application port and no external clients.</summary>
    private static async Task VerifyHttpAsync(ReadinessPort port, int status, string health, string configuration,
        string queue)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddScoped<IDeploymentAnalysisReadiness>(_ => port);
        builder.Services.AddHealthChecks().AddCheck<AnalysisReadinessHealthCheck>("ai_analysis",
            tags: [AnalysisHealthChecks.ReadinessTag], timeout: TimeSpan.FromMilliseconds(100));
        await using var app = builder.Build();
        app.MapApplicationHealthChecks();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var live = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(0, port.Calls);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(status, (int)ready.StatusCode);
        Assert.Equal(1, port.Calls);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-secret", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal(health, json.RootElement.GetProperty("status").GetString());
        Assert.Equal(configuration, json.RootElement.GetProperty("configuration").GetString());
        Assert.Equal(queue, json.RootElement.GetProperty("queue").GetString());
        Assert.Contains("no-store", ready.Headers.CacheControl!.ToString());
    }

    /// <summary>Strict application-port fake observes calls, cancellation and private dependency failures.</summary>
    private sealed class ReadinessPort : IDeploymentAnalysisReadiness
    {
        /// <summary>Safe snapshot for successful probes.</summary>
        public DeploymentAnalysisReadiness Snapshot { get; init; } = new(AnalysisConfigurationState.Disabled, true);

        /// <summary>Injects a sensitive dependency failure.</summary>
        public bool Fail { get; init; }

        /// <summary>Blocks until health middleware cancels its timeout token.</summary>
        public bool WaitForCancellation { get; init; }

        /// <summary>Number of application-port calls.</summary>
        public int Calls { get; private set; }

        /// <summary>Whether the running operation observed timeout cancellation.</summary>
        public bool CancellationObserved { get; private set; }

        /// <inheritdoc />
        public async Task<DeploymentAnalysisReadiness> CheckAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("private-secret");
            if (WaitForCancellation)
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }

            return Snapshot;
        }
    }
}