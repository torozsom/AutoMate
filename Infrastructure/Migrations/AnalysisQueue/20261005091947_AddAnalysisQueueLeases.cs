#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Analysis Queue Leases.
/// </summary>
public partial class AddAnalysisQueueLeases : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            "ix_deployment_analysis_work_items_completed_at_claimed_at",
            "deployment_analysis_work_items");

        migrationBuilder.AddColumn<int>(
            "attempt_count",
            "deployment_analysis_work_items",
            "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<Guid>(
            "lease_id",
            "deployment_analysis_work_items",
            "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            "lease_until",
            "deployment_analysis_work_items",
            "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            "ix_deployment_analysis_work_items_completed_at_lease_until_cre",
            "deployment_analysis_work_items",
            new[] { "completed_at", "lease_until", "created_at" });
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            "ix_deployment_analysis_work_items_completed_at_lease_until_cre",
            "deployment_analysis_work_items");

        migrationBuilder.DropColumn(
            "attempt_count",
            "deployment_analysis_work_items");

        migrationBuilder.DropColumn(
            "lease_id",
            "deployment_analysis_work_items");

        migrationBuilder.DropColumn(
            "lease_until",
            "deployment_analysis_work_items");

        migrationBuilder.CreateIndex(
            "ix_deployment_analysis_work_items_completed_at_claimed_at",
            "deployment_analysis_work_items",
            new[] { "completed_at", "claimed_at" });
    }
}