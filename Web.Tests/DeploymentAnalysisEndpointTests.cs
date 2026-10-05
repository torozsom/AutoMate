using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Application.Abstractions.Ai;
using Application.Data.Users;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Routes.Endpoints;
using Xunit;

namespace Web.Tests;

/// <summary>
///     Exercises the real DELETE/cancel routes, authorization middleware and antiforgery validator over loopback
///     HTTP.
/// </summary>
public sealed class DeploymentAnalysisEndpointTests
{
    /// <summary>Deletion requires authentication/antiforgery and maps the owner-scoped use-case result.</summary>
    [Theory]
    [InlineData(false, true, DeploymentAnalysisDeletionResult.Deleted, 401)]
    [InlineData(true, false, DeploymentAnalysisDeletionResult.Deleted, 400)]
    [InlineData(true, true, DeploymentAnalysisDeletionResult.Deleted, 204)]
    [InlineData(true, true, DeploymentAnalysisDeletionResult.NotFound, 404)]
    [InlineData(true, true, DeploymentAnalysisDeletionResult.InProgress, 409)]
    public async Task Delete_route_enforces_request_security_and_maps_outcomes(bool authenticated, bool csrf,
        DeploymentAnalysisDeletionResult outcome, int status)
    {
        await VerifyRouteAsync(authenticated, csrf, outcome, status, "DeleteAsync");
    }

    /// <summary>Cancellation uses authenticated ownership, antiforgery and fixed safe HTTP outcomes.</summary>
    [Theory]
    [InlineData(false, true, DeploymentAnalysisCancellationResult.Cancelled, 401, true)]
    [InlineData(true, false, DeploymentAnalysisCancellationResult.Cancelled, 400, true)]
    [InlineData(true, true, DeploymentAnalysisCancellationResult.Cancelled, 204, true)]
    [InlineData(true, true, DeploymentAnalysisCancellationResult.NotFound, 404, true)]
    [InlineData(true, true, DeploymentAnalysisCancellationResult.AlreadyFinished, 409, true)]
    [InlineData(true, true, DeploymentAnalysisCancellationResult.Cancelled, 403, false)]
    public async Task Cancel_route_enforces_request_security_and_maps_outcomes(bool authenticated, bool csrf,
        DeploymentAnalysisCancellationResult outcome, int status, bool validOwner)
    {
        await VerifyRouteAsync(authenticated, csrf, outcome, status, "CancelAsync", validOwner);
    }

    /// <summary>Runs production route/middleware on loopback with strict application-port fakes.</summary>
    private static async Task VerifyRouteAsync(bool authenticated, bool csrf, object outcome, int status,
        string methodName, bool validOwner = true)
    {
        var owner = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var calls = 0;
        var users = DispatchProxy.Create<IUserService, PortProxy>();
        ((PortProxy)users).Call = (_, args) =>
        {
            Assert.Equal(owner.ToString(), args![0]);
            return Task.FromResult((validOwner ? owner : Guid.Empty, (string?)null, false));
        };
        var analyses = DispatchProxy.Create<IDeploymentAnalysisService, PortProxy>();
        ((PortProxy)analyses).Call = (method, args) =>
        {
            Assert.Equal(methodName, method!.Name);
            Assert.Equal(owner, args![0]);
            Assert.Equal(deployment, args[1]);
            Assert.Equal(analysis, args[2]);
            calls++;
            return methodName == "CancelAsync"
                ? Task.FromResult((DeploymentAnalysisCancellationResult)outcome)
                : Task.FromResult((DeploymentAnalysisDeletionResult)outcome);
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("fixture")
            .AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton(analyses);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        new DeploymentAnalysisEndpoint().Map(app);
        app.MapGet("/fixture-token", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!));
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            { BaseAddress = new Uri(app.Urls.Single()) };
        if (authenticated) client.DefaultRequestHeaders.Add("Fixture-Owner", owner.ToString());
        var requestToken = await client.GetStringAsync("/fixture-token");
        if (csrf) client.DefaultRequestHeaders.Add("RequestVerificationToken", requestToken);
        var path = $"/api/deployments/{deployment}/analyses/{analysis}";
        var response = methodName == "CancelAsync"
            ? await client.PostAsync(path + "/cancel", null)
            : await client.DeleteAsync(path);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(authenticated && csrf && validOwner ? 1 : 0, calls);
    }

    /// <summary>Supplies application ports without contacting users, persistence or providers.</summary>
    public class PortProxy : DispatchProxy
    {
        /// <summary>Test-specific response behavior.</summary>
        public Func<MethodInfo?, object?[]?, object?> Call { get; set; } = null!;

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return Call(method, args);
        }
    }

    /// <summary>Authenticates only explicitly supplied synthetic test principals.</summary>
    private sealed class FixtureAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <inheritdoc />
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var owner = Request.Headers["Fixture-Owner"].ToString();
            if (string.IsNullOrEmpty(owner)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal =
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}