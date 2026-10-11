using Application.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Web.Configs;

/// <summary>Audits authorization challenges/denials while preserving the framework's challenge/forbid behavior.</summary>
public sealed class SecurityAuditResultHandler(ILogger<SecurityAuditResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    /// <summary>Default handler retains authentication scheme responses and redirects.</summary>
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
            OperationalLog.Record(logger, AuditOperation.Authorization, AuditOutcome.Challenged);
        else if (authorizeResult.Forbidden)
            OperationalLog.Record(logger, AuditOperation.Authorization, AuditOutcome.Denied);
        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}