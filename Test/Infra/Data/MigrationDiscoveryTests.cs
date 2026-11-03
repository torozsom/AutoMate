using Infrastructure.Data;
using Infrastructure.Migrations.AnalysisQueue;
using Infrastructure.Migrations.AnalysisResults;
using Infrastructure.Migrations.Checkpoints;
using Infrastructure.Migrations.CloudRuns;
using Infrastructure.Migrations.Identity;
using Infrastructure.Migrations.RetentionAndPolicy;
using Infrastructure.Migrations.Telemetry;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Test.Infra.Data;

/// <summary>Protects the complete historical migration registry without applying schema or data changes.</summary>
public sealed class MigrationDiscoveryTests
{
    /// <summary>EF discovers exactly the reviewed migration IDs/types and each forward migration remains executable metadata.</summary>
    [Fact]
    public void Historical_migration_chain_preserves_ids_types_and_operations()
    {
        (string Id, Migration Migration)[] expected =
        [
            ("20260522134720_Initial", new Initial()),
            ("20260524171414_RefactorRemoteUser", new RefactorRemoteUser()),
            ("20260928200050_AddGitHubWorkflowCheckpoints", new AddGitHubWorkflowCheckpoints()),
            ("20260928212104_AddAzureContainerAppLogCheckpoints", new AddAzureContainerAppLogCheckpoints()),
            ("20261002062913_AddDeploymentDiagnosticsAndAiAnalysis", new AddDeploymentDiagnosticsAndAiAnalysis()),
            ("20261002144610_AddTerminalLogReplay", new AddTerminalLogReplay()),
            ("20261003101739_AddSaasCloudRuns", new AddSaasCloudRuns()),
            ("20261003103139_AddCloudInstallationBudget", new AddCloudInstallationBudget()),
            ("20261003105303_AddCloudWebhookBranch", new AddCloudWebhookBranch()),
            ("20261003105945_AddCloudAdmissionIndexes", new AddCloudAdmissionIndexes()),
            ("20261003161839_AddSpecializedTelemetryStorage", new AddSpecializedTelemetryStorage()),
            ("20261003170209_AddTelemetryBufferAccounting", new AddTelemetryBufferAccounting()),
            ("20261004111715_AddSaaSTelemetryAnalytics", new AddSaaSTelemetryAnalytics()),
            ("20261004112450_AddCloudTelemetryCollectorLeases", new AddCloudTelemetryCollectorLeases()),
            ("20261004201350_AddAiAnalysisResultProvenance", new AddAiAnalysisResultProvenance()),
            ("20261005091947_AddAnalysisQueueLeases", new AddAnalysisQueueLeases()),
            ("20261005094754_AddAnalysisRetryScheduling", new AddAnalysisRetryScheduling()),
            ("20261005102239_AddAnalysisAdmissionReceipts", new AddAnalysisAdmissionReceipts()),
            ("20261005104851_AddFailedDeploymentAnalysisEvents", new AddFailedDeploymentAnalysisEvents()),
            ("20261005190054_AddAiTenantBudgets", new AddAiTenantBudgets()),
            ("20261008202231_PreserveDeploymentHistory", new PreserveDeploymentHistory()),
            ("20261009091530_StatusAwareAssessments", new StatusAwareAssessments()),
            ("20261010141504_AutomaticTelemetryCollection", new AutomaticTelemetryCollection()),
            ("20261011001842_AzureLogIngestionCursors", new AzureLogIngestionCursors())
        ];
        var options = new DbContextOptionsBuilder<AutoMateDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_migration_fixture").UseSnakeCaseNamingConvention().Options;
        using var db = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        var discovered = db.GetService<IMigrationsAssembly>();
        var provider = db.Database.ProviderName ??
                       throw new InvalidOperationException("A relational provider is required.");
        Assert.Equal(expected.Select(entry => entry.Id), discovered.Migrations.Keys);
        foreach (var entry in expected)
        {
            Assert.Equal(entry.Migration.GetType(), discovered.Migrations[entry.Id].AsType());
            entry.Migration.ActiveProvider = provider;
            Assert.NotEmpty(entry.Migration.UpOperations);
        }
    }
}
