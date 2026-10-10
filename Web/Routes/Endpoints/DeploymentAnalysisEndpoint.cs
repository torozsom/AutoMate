using System.Security.Claims;
using Application.Abstractions.Ai;
using Application.Ai;
using Application.Data.Users;
using Microsoft.AspNetCore.Antiforgery;

namespace Web.Routes.Endpoints;

/// <summary>Owner-authorized deletion and cancellation of analysis metadata with cookie-request forgery protection.</summary>
public sealed class DeploymentAnalysisEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/deployments/{deploymentId:guid}/analyses/preferences", PreferencesAsync)
            .RequireAuthorization();
        app.MapPut("/api/deployments/{deploymentId:guid}/analyses/preferences", SavePreferencesAsync)
            .RequireAuthorization();
        app.MapPost("/api/deployments/{deploymentId:guid}/analyses", RequestAsync).RequireAuthorization();
        app.MapDelete(
            "/api/deployments/{deploymentId:guid}/analyses/{analysisId:guid}", DeleteAsync).RequireAuthorization();
        app.MapPost(
                "/api/deployments/{deploymentId:guid}/analyses/{analysisId:guid}/cancel", CancelAsync)
            .RequireAuthorization();
    }

    /// <summary>Resolves ownership exclusively from the authenticated identifier.</summary>
    private static async Task<Guid> OwnerAsync(HttpContext context, IUserService users, CancellationToken token)
    {
        var identifier = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(identifier)
            ? Guid.Empty
            : (await users.GetUserDetailsFromIdentifierAsync(identifier, token)).UserId;
    }

    /// <summary>Returns only preferences belonging to the authenticated deployment owner.</summary>
    private static async Task<IResult> PreferencesAsync(HttpContext context, IUserService users,
        IDeploymentAnalysisService analyses, Guid deploymentId, CancellationToken token)
    {
        var owner = await OwnerAsync(context, users, token);
        if (owner == Guid.Empty) return Results.Forbid();
        var preferences = await analyses.GetPreferencesAsync(owner, deploymentId, token);
        return preferences is null ? Results.NotFound() : Results.Ok(preferences);
    }

    /// <summary>Protects mutable preferences with ownership, antiforgery and option validation.</summary>
    private static async Task<IResult> SavePreferencesAsync(HttpContext context, IUserService users,
        IDeploymentAnalysisService analyses, IAntiforgery antiforgery, Guid deploymentId,
        AssessmentSelection selection, CancellationToken token)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest("Invalid antiforgery token.");
        }

        var owner = await OwnerAsync(context, users, token);
        if (owner == Guid.Empty) return Results.Forbid();
        try
        {
            return await analyses.SavePreferencesAsync(owner, deploymentId, selection, token)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (ArgumentException)
        {
            return Results.BadRequest("Invalid assessment selection.");
        }
    }

    /// <summary>Admits immutable selected options and maps conflicting request-ID retries to HTTP 409.</summary>
    private static async Task<IResult> RequestAsync(HttpContext context, IUserService users,
        IDeploymentAnalysisService analyses, IAntiforgery antiforgery, Guid deploymentId,
        AssessmentRequest request, CancellationToken token)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest("Invalid antiforgery token.");
        }

        var owner = await OwnerAsync(context, users, token);
        if (owner == Guid.Empty) return Results.Forbid();
        if (request.Selection is null || request.RequestId == Guid.Empty)
            return Results.BadRequest("Invalid assessment request.");
        var result =
            await analyses.RequestManualAsync(owner, deploymentId, request.RequestId, request.Selection, token);
        return result.Conflict ? Results.Conflict(result) : Results.Ok(result);
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

    /// <summary>Immutable client admission data; owner identity always comes from authentication.</summary>
    public sealed record AssessmentRequest(Guid RequestId, AssessmentSelection Selection);
}