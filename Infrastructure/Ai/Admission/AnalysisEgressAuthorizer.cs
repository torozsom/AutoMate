using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>Reads fresh metadata for each gate; never trusts cached consent or caller-supplied tenant identifiers.</summary>
public sealed class AnalysisEgressAuthorizer(
    AutoMateDbContext db,
    IOptionsMonitor<AiAnalysisOptions> options,
    IDiagnosticRedactor redactor,
    AnalysisProviderCatalog? catalog = null)
    : IAnalysisEgressAuthorizer
{
    /// <inheritdoc />
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