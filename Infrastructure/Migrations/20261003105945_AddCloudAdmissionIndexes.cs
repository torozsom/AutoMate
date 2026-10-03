using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudAdmissionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_installation_id_phase_lease_until",
                table: "cloud_deployment_runs",
                columns: new[] { "installation_id", "phase", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_repository_id_branch_name_environment",
                table: "cloud_deployment_runs",
                columns: new[] { "repository_id", "branch_name", "environment_name", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_user_id_launch_started_at",
                table: "cloud_deployment_runs",
                columns: new[] { "user_id", "launch_started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_user_id_phase_lease_until",
                table: "cloud_deployment_runs",
                columns: new[] { "user_id", "phase", "lease_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cloud_deployment_runs_installation_id_phase_lease_until",
                table: "cloud_deployment_runs");

            migrationBuilder.DropIndex(
                name: "ix_cloud_deployment_runs_repository_id_branch_name_environment",
                table: "cloud_deployment_runs");

            migrationBuilder.DropIndex(
                name: "ix_cloud_deployment_runs_user_id_launch_started_at",
                table: "cloud_deployment_runs");

            migrationBuilder.DropIndex(
                name: "ix_cloud_deployment_runs_user_id_phase_lease_until",
                table: "cloud_deployment_runs");
        }
    }
}
