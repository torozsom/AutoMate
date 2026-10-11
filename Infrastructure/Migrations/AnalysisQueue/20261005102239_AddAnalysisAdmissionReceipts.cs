using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisAdmissionReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_analysis_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    admission_day = table.Column<DateOnly>(type: "date", nullable: false),
                    consumes_quota = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_analysis_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_analysis_requests_cs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "cs_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_analysis_requests_expires_at",
                table: "ai_analysis_requests",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_analysis_requests_project_id_admission_day_consumes_quota",
                table: "ai_analysis_requests",
                columns: new[] { "project_id", "admission_day", "consumes_quota" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_analysis_requests_request_key",
                table: "ai_analysis_requests",
                column: "request_key",
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analysis_requests");
        }
    }
}
