using Application.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Web.Configs;

/// <summary>
///     Audits authorization challenges/denials while preserving the framework's challenge/forbid
///     behavior.
/// </summary>
/// <param name="logger">The logger used for fixed operational events without diagnostic payloads.</param>
public sealed class SecurityAuditResultHandler(ILogger<SecurityAuditResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    /// <summary>Default handler retains authentication scheme responses and redirects.</summary>
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <summary>
    ///     Records finite authorization outcomes and delegates challenge/forbid handling to the
    ///     framework.
    /// </summary>
    /// <param name="next">Next middleware delegate.</param>
    /// <param name="context">Current authenticated HTTP request and response.</param>
    /// <param name="policy">Reviewed export or authorization policy.</param>
    /// <param name="authorizeResult">Framework authorization outcome to audit and execute.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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