using System.Security.Claims;
using Application.Abstractions.Diagnostics;
using Application.Data.Users;

namespace Web.Routes.Endpoints;

/// <summary>Authenticated bounded history APIs; provider query languages are never accepted.</summary>
public sealed class DeploymentHistoryEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/projects/{projectId:guid}/deployments/{deploymentId:guid}/logs",
            async (HttpContext context, IUserService users, IDeploymentHistoryService history,
                Guid projectId, Guid deploymentId, string? cursor, bool? backwards, int? limit, string? search,
                CancellationToken token) =>
            {
                var owner = await OwnerAsync(context, users, token);
                if (owner == Guid.Empty) return Results.Forbid();
                try
                {
                    return Results.Ok(await history.ReadLogsV2Async(owner, projectId, deploymentId, cursor,
                        backwards ?? true, limit ?? 500, search, token));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Forbid();
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest("Invalid history cursor or search.");
                }
            }).RequireAuthorization();
        app.MapGet("/api/v2/projects/{projectId:guid}/analytics",
            async (HttpContext context, IUserService users, IProjectTelemetryAnalytics analytics,
                Guid projectId, DateTimeOffset start, DateTimeOffset end, CancellationToken token) =>
            {
                var owner = await OwnerAsync(context, users, token);
                if (owner == Guid.Empty) return Results.Forbid();
                try
                {
                    return Results.Ok(await analytics.ReadAsync(owner, projectId, start, end, token));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Forbid();
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest("Invalid analytics date range.");
                }
            }).RequireAuthorization();
        app.MapGet("/api/projects/{projectId:guid}/deployments/{deploymentId:guid}/logs",
            async (HttpContext context, IUserService users, IDeploymentHistoryService history,
                Guid projectId, Guid deploymentId, long? cursor, bool? backwards, int? limit,
                CancellationToken token) =>
            {
                var owner = await OwnerAsync(context, users, token);
                if (owner == Guid.Empty) return Results.Forbid();
                try
                {
                    return Results.Ok(await history.ReadLogsAsync(owner, projectId, deploymentId, cursor ?? 0,
                        backwards ?? true, limit ?? 500, token));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Forbid();
                }
            }).RequireAuthorization();
        app.MapGet("/api/projects/{projectId:guid}/deployments/{deploymentId:guid}/metrics",
            async (HttpContext context, IUserService users, IDeploymentHistoryService history,
                Guid projectId, Guid deploymentId, DateTimeOffset start, DateTimeOffset end, int? maximumPoints,
                CancellationToken token) =>
            {
                var owner = await OwnerAsync(context, users, token);
                if (owner == Guid.Empty) return Results.Forbid();
                try
                {
                    return Results.Ok(await history.ReadMetricsAsync(owner, projectId, deploymentId, start, end,
                        maximumPoints ?? 500, token));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Forbid();
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest("Metric range must be positive and at most 30 days.");
                }
            }).RequireAuthorization();
    }

    /// <summary>Resolves GitHub and local principals through the existing user application contract.</summary>
    private static async Task<Guid> OwnerAsync(HttpContext context, IUserService users, CancellationToken token)
    {
        var identifier = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(identifier)) return Guid.Empty;
        return (await users.GetUserDetailsFromIdentifierAsync(identifier, token)).UserId;
    }
}