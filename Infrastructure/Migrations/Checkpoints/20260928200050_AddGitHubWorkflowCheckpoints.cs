using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubWorkflowCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "git_hub_workflow_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_run_id = table.Column<long>(type: "bigint", nullable: false),
                    workflow_attempt = table.Column<int>(type: "integer", nullable: false),
                    last_workflow_state_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    final_reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_hub_workflow_checkpoints", x => x.id);
                    table.ForeignKey(
                        name: "fk_git_hub_workflow_checkpoints_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "git_hub_workflow_job_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    git_hub_workflow_checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<long>(type: "bigint", nullable: false),
                    job_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    last_state_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_log_line_count = table.Column<int>(type: "integer", nullable: false),
                    last_log_prefix_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_log_content_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    final_archive_content_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    log_availability = table.Column<int>(type: "integer", nullable: false),
                    is_log_final = table.Column<bool>(type: "boolean", nullable: false),
                    last_log_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_hub_workflow_job_checkpoints", x => x.id);
                    table.ForeignKey(
                        name: "fk_git_hub_workflow_job_checkpoints_git_hub_workflow_checkpoin",
                        column: x => x.git_hub_workflow_checkpoint_id,
                        principalTable: "git_hub_workflow_checkpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_git_hub_workflow_checkpoints_deployment_id_workflow_run_id_",
                table: "git_hub_workflow_checkpoints",
                columns: new[] { "deployment_id", "workflow_run_id", "workflow_attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_git_hub_workflow_job_checkpoints_git_hub_workflow_checkpoin",
                table: "git_hub_workflow_job_checkpoints",
                columns: new[] { "git_hub_workflow_checkpoint_id", "job_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "git_hub_workflow_job_checkpoints");

            migrationBuilder.DropTable(
                name: "git_hub_workflow_checkpoints");
        }
    }
}
