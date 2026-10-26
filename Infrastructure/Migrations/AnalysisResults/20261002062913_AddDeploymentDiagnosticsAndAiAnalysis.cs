#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.AnalysisResults;

/// <summary>
///     EF schema migration and target-model metadata for Add Deployment Diagnostics And Ai
///     Analysis.
/// </summary>
public partial class AddDeploymentDiagnosticsAndAiAnalysis : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            "ai_diagnostic_egress_consented",
            "app_configs",
            "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            "ai_deployment_analyses",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                deployment_id = table.Column<Guid>("uuid", nullable: false),
                trigger = table.Column<int>("integer", nullable: false),
                status = table.Column<int>("integer", nullable: false),
                provider = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
                model = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
                idempotency_key = table.Column<string>("character varying(128)", maxLength: 128, nullable: false),
                retry_count = table.Column<int>("integer", nullable: false),
                summary = table.Column<string>("text", nullable: true),
                recommended_steps_json = table.Column<string>("text", nullable: true),
                evidence_references_json = table.Column<string>("text", nullable: true),
                failure_code = table.Column<string>("character varying(100)", maxLength: 100, nullable: true),
                started_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                expires_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ai_deployment_analyses", x => x.id);
                table.ForeignKey(
                    "fk_ai_deployment_analyses_deployments_deployment_id",
                    x => x.deployment_id,
                    "deployments",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            "deployment_diagnostic_records",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                deployment_id = table.Column<Guid>("uuid", nullable: true),
                project_id = table.Column<Guid>("uuid", nullable: false),
                timestamp_utc = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                source = table.Column<string>("character varying(64)", maxLength: 64, nullable: false),
                kind = table.Column<string>("character varying(64)", maxLength: 64, nullable: false),
                severity = table.Column<string>("character varying(32)", maxLength: 32, nullable: false),
                message = table.Column<string>("text", nullable: false),
                attributes_json = table.Column<string>("text", nullable: true),
                trace_id = table.Column<string>("text", nullable: true),
                span_id = table.Column<string>("text", nullable: true),
                sequence = table.Column<long>("bigint", nullable: true),
                cursor = table.Column<string>("text", nullable: true),
                expires_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_deployment_diagnostic_records", x => x.id);
                table.ForeignKey(
                    "fk_deployment_diagnostic_records_deployments_deployment_id",
                    x => x.deployment_id,
                    "deployments",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            "deployment_analysis_work_items",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                analysis_id = table.Column<Guid>("uuid", nullable: false),
                claimed_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_deployment_analysis_work_items", x => x.id);
                table.ForeignKey(
                    "fk_deployment_analysis_work_items_ai_deployment_analyses_analy",
                    x => x.analysis_id,
                    "ai_deployment_analyses",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_ai_deployment_analyses_deployment_id_created_at",
            "ai_deployment_analyses",
            ["deployment_id", "created_at"]);

        migrationBuilder.CreateIndex(
            "ix_ai_deployment_analyses_expires_at",
            "ai_deployment_analyses",
            "expires_at");

        migrationBuilder.CreateIndex(
            "ix_ai_deployment_analyses_idempotency_key",
            "ai_deployment_analyses",
            "idempotency_key",
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_deployment_analysis_work_items_analysis_id",
            "deployment_analysis_work_items",
            "analysis_id",
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_deployment_analysis_work_items_completed_at_claimed_at",
            "deployment_analysis_work_items",
            ["completed_at", "claimed_at"]);

        migrationBuilder.CreateIndex(
            "ix_deployment_diagnostic_records_deployment_id_timestamp_utc_s",
            "deployment_diagnostic_records",
            ["deployment_id", "timestamp_utc", "sequence"]);

        migrationBuilder.CreateIndex(
            "ix_deployment_diagnostic_records_expires_at",
            "deployment_diagnostic_records",
            "expires_at");
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "deployment_analysis_work_items");

        migrationBuilder.DropTable(
            "deployment_diagnostic_records");

        migrationBuilder.DropTable(
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "ai_diagnostic_egress_consented",
            "app_configs");
    }
}
