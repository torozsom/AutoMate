#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Analysis Admission Receipts.
/// </summary>
public partial class AddAnalysisAdmissionReceipts : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "ai_analysis_requests",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                project_id = table.Column<Guid>("uuid", nullable: false),
                deployment_id = table.Column<Guid>("uuid", nullable: false),
                analysis_id = table.Column<Guid>("uuid", nullable: false),
                request_key = table.Column<string>("character varying(128)", maxLength: 128, nullable: false),
                admission_day = table.Column<DateOnly>("date", nullable: false),
                consumes_quota = table.Column<bool>("boolean", nullable: false),
                expires_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ai_analysis_requests", x => x.id);
                table.ForeignKey(
                    "fk_ai_analysis_requests_cs_projects_project_id",
                    x => x.project_id,
                    "cs_projects",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_ai_analysis_requests_expires_at",
            "ai_analysis_requests",
            "expires_at");

        migrationBuilder.CreateIndex(
            "ix_ai_analysis_requests_project_id_admission_day_consumes_quota",
            "ai_analysis_requests",
            new[] { "project_id", "admission_day", "consumes_quota" });

        migrationBuilder.CreateIndex(
            "ix_ai_analysis_requests_request_key",
            "ai_analysis_requests",
            "request_key",
            unique: true);

        // Preserve known admission usage for surviving legacy analyses without retaining any diagnostic payload.
        // Historical analyses already deleted before this migration cannot be reconstructed.
        migrationBuilder.Sql("""
                             INSERT INTO ai_analysis_requests
                                 (id, project_id, deployment_id, analysis_id, request_key, admission_day, consumes_quota,
                                  expires_at, created_at, updated_at)
                             SELECT a.id, d.cs_project_id, a.deployment_id, a.id, 'legacy:' || a.id::text,
                                    (a.created_at AT TIME ZONE 'UTC')::date, TRUE,
                                    a.created_at + INTERVAL '90 days', a.created_at, a.updated_at
                             FROM ai_deployment_analyses AS a
                             INNER JOIN deployments AS d ON d.id = a.deployment_id
                             WHERE a.created_at + INTERVAL '90 days' > CURRENT_TIMESTAMP;
                             """);
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "ai_analysis_requests");
    }
}