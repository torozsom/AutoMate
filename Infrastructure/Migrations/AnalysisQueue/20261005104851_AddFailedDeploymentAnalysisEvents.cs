#nullable disable

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.AnalysisQueue;

/// <summary>
///     EF schema migration and target-model metadata for Add Failed Deployment Analysis Events.
/// </summary>
public partial class AddFailedDeploymentAnalysisEvents : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "failed_deployment_analysis_events",
            table => new
            {
                deployment_id = table.Column<Guid>("uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_failed_deployment_analysis_events", x => x.deployment_id);
                table.ForeignKey(
                    "fk_failed_deployment_analysis_events_deployments_deployment_id",
                    x => x.deployment_id,
                    "deployments",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_failed_deployment_analysis_events_completed_at_created_at",
            "failed_deployment_analysis_events",
            new[] { "completed_at", "created_at" });

        migrationBuilder.Sql(FailedDeploymentAnalysisTrigger.CreateSql);
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FailedDeploymentAnalysisTrigger.DropSql);

        migrationBuilder.DropTable(
            "failed_deployment_analysis_events");
    }
}
