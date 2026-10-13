using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>
///     Reads fresh metadata for each gate; never trusts cached consent or caller-supplied
///     tenant identifiers.
/// </summary>
/// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="catalog">Allowlisted provider registry used for adapter selection.</param>
public sealed class AnalysisEgressAuthorizer(
    AutoMateDbContext db,
    IOptionsMonitor<AiAnalysisOptions> options,
    IDiagnosticRedactor redactor,
    AnalysisProviderCatalog? catalog = null)
    : IAnalysisEgressAuthorizer
{
    /// <summary>
    ///     Returns false for missing deployments, unapproved tenants, revoked consent or disabled
    ///     triggers.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="trigger">
    ///     The manual or automatic trigger; it does not grant provider egress permission.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning true only when current ownership, consent and operator policy permit
    ///     egress.
    /// </returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public async Task<bool> AuthorizeAsync(Guid deploymentId, AiAnalysisTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.CurrentValue;
        if (!(catalog?.IsConfigured(settings) ?? AnalysisEgressPolicy.IsConfigured(settings)) ||
            redactor.RedactText(settings.Model, 128) != settings.Model ||
            trigger is not (AiAnalysisTrigger.Manual or AiAnalysisTrigger.DeploymentFailed) ||
            (trigger == AiAnalysisTrigger.DeploymentFailed && !settings.AutomaticAnalysisEnabled)) return false;
        var deployment = await db.Deployments.AsNoTracking().Where(item => item.Id == deploymentId)
            .Select(item => new
            {
                Owner = item.CsProject!.Application.UserId,
                Consent = item.CsProject.Configuration != null &&
                          item.CsProject.Configuration.AiDiagnosticEgressConsented
            }).SingleOrDefaultAsync(cancellationToken);
        return ReferenceEquals(settings, options.CurrentValue) && deployment is not null && deployment.Consent &&
               settings.ApprovedTenantIds.Contains(deployment.Owner);
    }
}
