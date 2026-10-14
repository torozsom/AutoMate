#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.CloudRuns;

/// <summary>
///     EF schema migration and target-model metadata for Add Saas Cloud Runs.
/// </summary>
public partial class AddSaasCloudRuns : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "cloud_deployment_runs",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                user_id = table.Column<Guid>("uuid", nullable: false),
                project_id = table.Column<Guid>("uuid", nullable: false),
                deployment_id = table.Column<Guid>("uuid", nullable: true),
                idempotency_key = table.Column<string>("character varying(128)", maxLength: 128, nullable: false),
                installation_id = table.Column<long>("bigint", nullable: false),
                repository_id = table.Column<long>("bigint", nullable: false),
                repository_owner = table.Column<string>("character varying(255)", maxLength: 255, nullable: false),
                repository_name = table.Column<string>("character varying(255)", maxLength: 255, nullable: false),
                branch_name = table.Column<string>("character varying(255)", maxLength: 255, nullable: false),
                environment_name = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
                workflow_file_name = table.Column<string>("character varying(255)", maxLength: 255, nullable: false),
                registry_server = table.Column<string>("character varying(255)", maxLength: 255, nullable: false),
                snapshot_json = table.Column<string>("text", nullable: true),
                phase = table.Column<string>("character varying(32)", maxLength: 32, nullable: false),
                commit_sha = table.Column<string>("character varying(64)", maxLength: 64, nullable: true),
                workflow_run_id = table.Column<long>("bigint", nullable: true),
                attempt = table.Column<int>("integer", nullable: false),
                next_attempt_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                lease_owner = table.Column<Guid>("uuid", nullable: true),
                lease_until = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                launch_started_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                committed_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                failure_reason = table.Column<string>("character varying(512)", maxLength: 512, nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cloud_deployment_runs", x => x.id);
                table.ForeignKey(
                    "fk_cloud_deployment_runs_applications_project_id",
                    x => x.project_id,
                    "applications",
                    "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    "fk_cloud_deployment_runs_users_user_id",
                    x => x.user_id,
                    "users",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            "cloud_webhook_deliveries",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                delivery_id = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
                installation_id = table.Column<long>("bigint", nullable: false),
                repository_id = table.Column<long>("bigint", nullable: false),
                workflow_run_id = table.Column<long>("bigint", nullable: false),
                workflow_attempt = table.Column<int>("integer", nullable: false),
                head_sha = table.Column<string>("character varying(64)", maxLength: 64, nullable: false),
                workflow_path = table.Column<string>("character varying(512)", maxLength: 512, nullable: false),
                status = table.Column<string>("character varying(32)", maxLength: 32, nullable: false),
                conclusion = table.Column<string>("character varying(32)", maxLength: 32, nullable: true),
                processed_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                lease_owner = table.Column<Guid>("uuid", nullable: true),
                lease_until = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table => { table.PrimaryKey("pk_cloud_webhook_deliveries", x => x.id); });

        migrationBuilder.CreateTable(
            "cloud_run_outbox",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                run_id = table.Column<Guid>("uuid", nullable: false),
                dispatched_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cloud_run_outbox", x => x.id);
                table.ForeignKey(
                    "fk_cloud_run_outbox_cloud_deployment_runs_run_id",
                    x => x.run_id,
                    "cloud_deployment_runs",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_installation_id_repository_id_commit_",
            "cloud_deployment_runs",
            new[] { "installation_id", "repository_id", "commit_sha" });

        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_phase_next_attempt_at_created_at",
            "cloud_deployment_runs",
            new[] { "phase", "next_attempt_at", "created_at" });

        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_project_id_created_at",
            "cloud_deployment_runs",
            new[] { "project_id", "created_at" });

        migrationBuilder.CreateIndex(
            "ix_cloud_deployment_runs_user_id_idempotency_key",
            "cloud_deployment_runs",
            new[] { "user_id", "idempotency_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_cloud_run_outbox_dispatched_at",
            "cloud_run_outbox",
            "dispatched_at");

        migrationBuilder.CreateIndex(
            "ix_cloud_run_outbox_run_id",
            "cloud_run_outbox",
            "run_id",
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_cloud_webhook_deliveries_delivery_id",
            "cloud_webhook_deliveries",
            "delivery_id",
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_cloud_webhook_deliveries_processed_at_created_at",
            "cloud_webhook_deliveries",
            new[] { "processed_at", "created_at" });
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "cloud_run_outbox");

        migrationBuilder.DropTable(
            "cloud_webhook_deliveries");

        migrationBuilder.DropTable(
            "cloud_deployment_runs");
    }
}
