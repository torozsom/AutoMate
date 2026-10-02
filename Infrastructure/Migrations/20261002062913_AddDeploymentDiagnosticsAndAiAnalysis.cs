using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentDiagnosticsAndAiAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ai_diagnostic_egress_consented",
                table: "app_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ai_deployment_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    recommended_steps_json = table.Column<string>(type: "text", nullable: true),
                    evidence_references_json = table.Column<string>(type: "text", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_deployment_analyses", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_deployment_analyses_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_diagnostic_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    attributes_json = table.Column<string>(type: "text", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    span_id = table.Column<string>(type: "text", nullable: true),
                    sequence = table.Column<long>(type: "bigint", nullable: true),
                    cursor = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_diagnostic_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_diagnostic_records_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_analysis_work_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_analysis_work_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_analysis_work_items_ai_deployment_analyses_analy",
                        column: x => x.analysis_id,
                        principalTable: "ai_deployment_analyses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_deployment_analyses_deployment_id_created_at",
                table: "ai_deployment_analyses",
                columns: new[] { "deployment_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_deployment_analyses_expires_at",
                table: "ai_deployment_analyses",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_deployment_analyses_idempotency_key",
                table: "ai_deployment_analyses",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_analysis_work_items_analysis_id",
                table: "deployment_analysis_work_items",
                column: "analysis_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_analysis_work_items_completed_at_claimed_at",
                table: "deployment_analysis_work_items",
                columns: new[] { "completed_at", "claimed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnostic_records_deployment_id_timestamp_utc_s",
                table: "deployment_diagnostic_records",
                columns: new[] { "deployment_id", "timestamp_utc", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnostic_records_expires_at",
                table: "deployment_diagnostic_records",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_analysis_work_items");

            migrationBuilder.DropTable(
                name: "deployment_diagnostic_records");

            migrationBuilder.DropTable(
                name: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "ai_diagnostic_egress_consented",
                table: "app_configs");
        }
    }
}
