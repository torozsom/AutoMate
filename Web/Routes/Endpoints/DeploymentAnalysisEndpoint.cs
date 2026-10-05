using System.Security.Claims;
using Application.Abstractions.Ai;
using Application.Data.Users;
using Microsoft.AspNetCore.Antiforgery;

namespace Web.Routes.Endpoints;

/// <summary>Owner-authorized deletion and cancellation of analysis metadata with cookie-request forgery protection.</summary>
public sealed class DeploymentAnalysisEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapDelete(
            "/api/deployments/{deploymentId:guid}/analyses/{analysisId:guid}", DeleteAsync).RequireAuthorization();
        app.MapPost(
                "/api/deployments/{deploymentId:guid}/analyses/{analysisId:guid}/cancel", CancelAsync)
            .RequireAuthorization();
    }

    /// <summary>Resolves the current owner and validates antiforgery before any deletion.</summary>
    internal static async Task<IResult> DeleteAsync(HttpContext context, IUserService users,
        IDeploymentAnalysisService analyses, IAntiforgery antiforgery, Guid deploymentId, Guid analysisId,
        CancellationToken token)
    {
        var identifier = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(identifier)) return Results.Forbid();
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest("Invalid antiforgery token.");
        }

        var owner = (await users.GetUserDetailsFromIdentifierAsync(identifier, token)).UserId;
        if (owner == Guid.Empty) return Results.Forbid();
        return await analyses.DeleteAsync(owner, deploymentId, analysisId, token) switch
        {
            DeploymentAnalysisDeletionResult.Deleted => Results.NoContent(),
            DeploymentAnalysisDeletionResult.InProgress => Results.Conflict("Analysis is still in progress."),
            _ => Results.NotFound()
        };
    }

    /// <summary>Resolves authenticated ownership and validates antiforgery before durable cancellation.</summary>
    internal static async Task<IResult> CancelAsync(HttpContext context, IUserService users,
        IDeploymentAnalysisService analyses, IAntiforgery antiforgery, Guid deploymentId, Guid analysisId,
        CancellationToken token)
    {
        var identifier = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(identifier)) return Results.Forbid();
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest("Invalid antiforgery token.");
        }

        var owner = (await users.GetUserDetailsFromIdentifierAsync(identifier, token)).UserId;
        if (owner == Guid.Empty) return Results.Forbid();
        return await analyses.CancelAsync(owner, deploymentId, analysisId, token) switch
        {
            DeploymentAnalysisCancellationResult.Cancelled => Results.NoContent(),
            DeploymentAnalysisCancellationResult.AlreadyFinished => Results.Conflict("Analysis has already finished."),
            _ => Results.NotFound()
        };
    }
}