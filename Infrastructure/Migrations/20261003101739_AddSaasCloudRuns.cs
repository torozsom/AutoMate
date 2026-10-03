using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSaasCloudRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cloud_deployment_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_owner = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    repository_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    branch_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    environment_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    workflow_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    registry_server = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    snapshot_json = table.Column<string>(type: "text", nullable: true),
                    phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    workflow_run_id = table.Column<long>(type: "bigint", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    launch_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cloud_deployment_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_cloud_deployment_runs_applications_project_id",
                        column: x => x.project_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cloud_deployment_runs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cloud_webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    workflow_run_id = table.Column<long>(type: "bigint", nullable: false),
                    workflow_attempt = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    workflow_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    conclusion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cloud_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cloud_run_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cloud_run_outbox", x => x.id);
                    table.ForeignKey(
                        name: "fk_cloud_run_outbox_cloud_deployment_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "cloud_deployment_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_installation_id_repository_id_commit_",
                table: "cloud_deployment_runs",
                columns: new[] { "installation_id", "repository_id", "commit_sha" });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_phase_next_attempt_at_created_at",
                table: "cloud_deployment_runs",
                columns: new[] { "phase", "next_attempt_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_project_id_created_at",
                table: "cloud_deployment_runs",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_deployment_runs_user_id_idempotency_key",
                table: "cloud_deployment_runs",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cloud_run_outbox_dispatched_at",
                table: "cloud_run_outbox",
                column: "dispatched_at");

            migrationBuilder.CreateIndex(
                name: "ix_cloud_run_outbox_run_id",
                table: "cloud_run_outbox",
                column: "run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cloud_webhook_deliveries_delivery_id",
                table: "cloud_webhook_deliveries",
                column: "delivery_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cloud_webhook_deliveries_processed_at_created_at",
                table: "cloud_webhook_deliveries",
                columns: new[] { "processed_at", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cloud_run_outbox");

            migrationBuilder.DropTable(
                name: "cloud_webhook_deliveries");

            migrationBuilder.DropTable(
                name: "cloud_deployment_runs");
        }
    }
}
