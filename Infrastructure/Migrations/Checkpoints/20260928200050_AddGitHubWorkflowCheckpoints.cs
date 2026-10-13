#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Git Hub Workflow Checkpoints.
/// </summary>
public partial class AddGitHubWorkflowCheckpoints : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "git_hub_workflow_checkpoints",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                deployment_id = table.Column<Guid>("uuid", nullable: false),
                workflow_run_id = table.Column<long>("bigint", nullable: false),
                workflow_attempt = table.Column<int>("integer", nullable: false),
                last_workflow_state_fingerprint =
                    table.Column<string>("character varying(128)", maxLength: 128, nullable: true),
                final_reconciled_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_git_hub_workflow_checkpoints", x => x.id);
                table.ForeignKey(
                    "fk_git_hub_workflow_checkpoints_deployments_deployment_id",
                    x => x.deployment_id,
                    "deployments",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            "git_hub_workflow_job_checkpoints",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                git_hub_workflow_checkpoint_id = table.Column<Guid>("uuid", nullable: false),
                job_id = table.Column<long>("bigint", nullable: false),
                job_name = table.Column<string>("character varying(512)", maxLength: 512, nullable: false),
                last_state_fingerprint = table.Column<string>("character varying(128)", maxLength: 128, nullable: true),
                last_log_line_count = table.Column<int>("integer", nullable: false),
                last_log_prefix_hash = table.Column<string>("character varying(128)", maxLength: 128, nullable: true),
                last_log_content_hash = table.Column<string>("character varying(128)", maxLength: 128, nullable: true),
                final_archive_content_hash =
                    table.Column<string>("character varying(128)", maxLength: 128, nullable: true),
                log_availability = table.Column<int>("integer", nullable: false),
                is_log_final = table.Column<bool>("boolean", nullable: false),
                last_log_checked_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_git_hub_workflow_job_checkpoints", x => x.id);
                table.ForeignKey(
                    "fk_git_hub_workflow_job_checkpoints_git_hub_workflow_checkpoin",
                    x => x.git_hub_workflow_checkpoint_id,
                    "git_hub_workflow_checkpoints",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_git_hub_workflow_checkpoints_deployment_id_workflow_run_id_",
            "git_hub_workflow_checkpoints",
            new[] { "deployment_id", "workflow_run_id", "workflow_attempt" },
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_git_hub_workflow_job_checkpoints_git_hub_workflow_checkpoin",
            "git_hub_workflow_job_checkpoints",
            new[] { "git_hub_workflow_checkpoint_id", "job_id" },
            unique: true);
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "git_hub_workflow_job_checkpoints");

        migrationBuilder.DropTable(
            "git_hub_workflow_checkpoints");
    }
}
