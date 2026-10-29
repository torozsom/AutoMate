using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Application.Abstractions.Ai.Execution;
using Application.Ai.Context;
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

namespace Test.Web.Analysis;

/// <summary>
///     Exercises the real DELETE/cancel routes, authorization middleware and antiforgery validator
///     over loopback HTTP.
/// </summary>
public sealed class DeploymentAnalysisEndpointTests
{
    /// <summary>
    ///     New admissions require authentication/antiforgery and expose immutable option conflicts.
    /// </summary>
    /// <param name="authenticated">Whether the synthetic principal is authenticated.</param>
    /// <param name="csrf">Whether valid antiforgery proof is supplied.</param>
    /// <param name="conflict">Whether the use case reports an active-operation conflict.</param>
    /// <param name="status">Synthetic HTTP response status.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false, true, false, 401)]
    [InlineData(true, false, false, 400)]
    [InlineData(true, true, false, 200)]
    [InlineData(true, true, true, 409)]
    public async Task Assessment_request_enforces_security_and_maps_option_conflicts(bool authenticated, bool csrf,
        bool conflict, int status)
    {
        await VerifyRouteAsync(authenticated, csrf,
            new DeploymentAnalysisRequestResult(!conflict, "fixed guidance", null, conflict), status,
            "RequestManualAsync");
    }

    /// <summary>
    ///     Preference mutations preserve authenticated owner and forgery checks.
    /// </summary>
    /// <param name="authenticated">Whether the synthetic principal is authenticated.</param>
    /// <param name="csrf">Whether valid antiforgery proof is supplied.</param>
    /// <param name="exists">Whether the owned resource exists.</param>
    /// <param name="status">Synthetic HTTP response status.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false, true, true, 401)]
    [InlineData(true, false, true, 400)]
    [InlineData(true, true, true, 204)]
    [InlineData(true, true, false, 404)]
    public async Task Assessment_preferences_enforce_security_and_owner_scoped_write(bool authenticated, bool csrf,
        bool exists, int status)
    {
        await VerifyRouteAsync(authenticated, csrf, exists, status, "SavePreferencesAsync");
    }

    /// <summary>
    ///     Deletion requires authentication/antiforgery and maps the owner-scoped use-case result.
    /// </summary>
    /// <param name="authenticated">Whether the synthetic principal is authenticated.</param>
    /// <param name="csrf">Whether valid antiforgery proof is supplied.</param>
    /// <param name="outcome">Configured or expected operation outcome.</param>
    /// <param name="status">Synthetic HTTP response status.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Cancellation uses authenticated ownership, antiforgery and fixed safe HTTP outcomes.
    /// </summary>
    /// <param name="authenticated">Whether the synthetic principal is authenticated.</param>
    /// <param name="csrf">Whether valid antiforgery proof is supplied.</param>
    /// <param name="outcome">Configured or expected operation outcome.</param>
    /// <param name="status">Synthetic HTTP response status.</param>
    /// <param name="validOwner">Whether the principal resolves to the expected owner.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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

    /// <summary>
    ///     Runs production route/middleware on loopback with strict application-port fakes.
    /// </summary>
    /// <param name="authenticated">Whether the synthetic principal is authenticated.</param>
    /// <param name="csrf">Whether valid antiforgery proof is supplied.</param>
    /// <param name="outcome">Configured or expected operation outcome.</param>
    /// <param name="status">Synthetic HTTP response status.</param>
    /// <param name="methodName">Private component method to invoke.</param>
    /// <param name="validOwner">Whether the principal resolves to the expected owner.</param>
    /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
    private static async Task VerifyRouteAsync(bool authenticated, bool csrf, object outcome, int status,
        string methodName, bool validOwner = true)
    {
        var owner = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var calls = 0;
        var users = DispatchProxy.Create<IUserService, PortProxy>();
        var userLookups = new List<object?[]?>();

        (Assert.IsAssignableFrom<PortProxy>(users)).Call = (_, args) =>
        {
            userLookups.Add(args);

            return Task.FromResult((validOwner ? owner : Guid.Empty, (string?)null, false));
        };
        var analyses = DispatchProxy.Create<IDeploymentAnalysisService, PortProxy>();

        (Assert.IsAssignableFrom<PortProxy>(analyses)).Call = (method, args) =>
        {
            Assert.Equal(methodName, method!.Name);
            Assert.Equal(owner, args![0]);
            Assert.Equal(deployment, args[1]);

            if (methodName == "SavePreferencesAsync") Assert.IsType<AssessmentSelection>(args[2]);
            else Assert.Equal(analysis, args[2]);
            calls++;

            return method.Name switch
            {
                "CancelAsync" => Task.FromResult((DeploymentAnalysisCancellationResult)outcome),
                "RequestManualAsync" => Task.FromResult((DeploymentAnalysisRequestResult)outcome),
                "SavePreferencesAsync" => Task.FromResult((bool)outcome),
                _ => Task.FromResult((DeploymentAnalysisDeletionResult)outcome)
            };
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
            ;
        client.BaseAddress = new Uri(app.Urls.Single());

        if (authenticated) client.DefaultRequestHeaders.Add("Fixture-Owner", owner.ToString());
        var requestToken = await client.GetStringAsync("/fixture-token");

        if (csrf) client.DefaultRequestHeaders.Add("RequestVerificationToken", requestToken);
        var path = $"/api/deployments/{deployment}/analyses/{analysis}";
        var response = methodName switch
        {
            "CancelAsync" => await client.PostAsync(path + "/cancel", null),
            "RequestManualAsync" => await client.PostAsJsonAsync($"/api/deployments/{deployment}/analyses",
                new DeploymentAnalysisEndpoint.AssessmentRequest(analysis, new AssessmentSelection())),
            "SavePreferencesAsync" => await client.PutAsJsonAsync($"/api/deployments/{deployment}/analyses/preferences",
                new AssessmentSelection()),
            _ => await client.DeleteAsync(path)
        };

        if (authenticated && csrf) Assert.Equal(owner.ToString(), Assert.Single(userLookups)![0]);
        else Assert.Empty(userLookups);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(authenticated && csrf && validOwner ? 1 : 0, calls);
    }

    /// <summary>
    ///     Supplies application ports without contacting users, persistence or providers.
    /// </summary>
    public class PortProxy : DispatchProxy
    {
        /// <summary>Test-specific response behavior.</summary>
        public Func<MethodInfo?, object?[]?, object?> Call { get; set; } = null!;

        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="method">Reflected member or client method selected for invocation.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return Call(method, args);
        }
    }

    /// <summary>
    ///     Authenticates only explicitly supplied synthetic test principals.
    /// </summary>
    /// <param name="options">Authentication options monitor supplied by the framework.</param>
    /// <param name="logger">The logger used for fixed operational events without diagnostic payloads.</param>
    /// <param name="encoder">Framework URL encoder required by fixture authentication.</param>
    private sealed class FixtureAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>
        ///     Builds a synthetic principal from the fixture owner header, or supplies no authentication
        ///     result.
        /// </summary>
        /// <returns>A task returning the configured authenticate result instance or response for this scenario.</returns>
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
