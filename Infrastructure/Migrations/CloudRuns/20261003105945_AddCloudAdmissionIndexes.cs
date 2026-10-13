#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Cloud Admission Indexes.
/// </summary>
public partial class AddCloudAdmissionIndexes : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_installation_id_phase_lease_until",
            "cloud_deployment_runs",
            new[] { "installation_id", "phase", "lease_until" });

        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_repository_id_branch_name_environment",
            "cloud_deployment_runs",
            new[] { "repository_id", "branch_name", "environment_name", "created_at" });

        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_user_id_launch_started_at",
            "cloud_deployment_runs",
            new[] { "user_id", "launch_started_at" });

        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_user_id_phase_lease_until",
            "cloud_deployment_runs",
            new[] { "user_id", "phase", "lease_until" });
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            "ix_cloud_deployment_runs_installation_id_phase_lease_until",
            "cloud_deployment_runs");

        migrationBuilder.DropIndex(
            "ix_cloud_deployment_runs_repository_id_branch_name_environment",
            "cloud_deployment_runs");

        migrationBuilder.DropIndex(
            "ix_cloud_deployment_runs_user_id_launch_started_at",
            "cloud_deployment_runs");

        migrationBuilder.DropIndex(
            "ix_cloud_deployment_runs_user_id_phase_lease_until",
            "cloud_deployment_runs");
    }
}
