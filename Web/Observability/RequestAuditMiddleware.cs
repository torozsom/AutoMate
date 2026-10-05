using System.Diagnostics;
using System.Security.Claims;

namespace Web.Observability;

/// <summary>Audits HTTP operation boundaries without recording URLs, queries, headers, cookies or request bodies.</summary>
public sealed class RequestAuditMiddleware(RequestDelegate next, ILogger<RequestAuditMiddleware> logger)
{
    /// <summary>Records starts, response outcomes and exceptions while preserving request behavior.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var area = Area(context.Request.Path);
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId });
        logger.LogInformation("Request started: method {HttpMethod}, area {RequestArea}, request {RequestId}.",
            context.Request.Method, area, requestId);
        try
        {
            await next(context);
            var userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                ? id : (Guid?)null;
            logger.Log(context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information,
                "Request finished: method {HttpMethod}, area {RequestArea}, request {RequestId}, status {StatusCode}, duration {DurationMs} ms, authentication {AuthenticationState}, user {UserId}.",
                context.Request.Method, area, requestId, context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                context.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous", userId);
        }
        catch (Exception exception)
        {
            logger.Log(context.RequestAborted.IsCancellationRequested ? LogLevel.Information : LogLevel.Error,
                "Request failed: request {RequestId}, duration {DurationMs} ms, failure {FailureType}.",
                requestId, Stopwatch.GetElapsedTime(started).TotalMilliseconds, exception.GetType().Name);
            throw;
        }
    }

    /// <summary>Uses finite route areas rather than customer-controlled path or endpoint text.</summary>
    private static string Area(PathString path)
    {
        if (path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/signin-github") ||
            path.StartsWithSegments("/signin-microsoft")) return "Authentication";
        if (path.StartsWithSegments("/api/deployments")) return "Deployments";
        if (path.StartsWithSegments("/loghub") || path.StartsWithSegments("/_blazor")) return "SignalR";
        if (path.StartsWithSegments("/health")) return "Health";
        return "Application";
    }
}
