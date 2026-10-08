using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiTenantBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_analysis_budget_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_provider_attempt = table.Column<bool>(type: "boolean", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accounting_day = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reserved_cost_units = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_analysis_budget_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_analysis_budget_entries_users_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_analysis_budget_entries_lease_id",
                table: "ai_analysis_budget_entries",
                column: "lease_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_analysis_budget_entries_occurred_at",
                table: "ai_analysis_budget_entries",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_analysis_budget_entries_tenant_id_accounting_day_is_prov",
                table: "ai_analysis_budget_entries",
                columns: new[] { "tenant_id", "accounting_day", "is_provider_attempt" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_analysis_budget_entries_tenant_id_is_provider_attempt_oc",
                table: "ai_analysis_budget_entries",
                columns: new[] { "tenant_id", "is_provider_attempt", "occurred_at" });

            // Preserve retained admission usage; historical remote charges cannot be inferred from optional result estimates.
            migrationBuilder.Sql("""
                INSERT INTO ai_analysis_budget_entries
                    (id, tenant_id, analysis_id, is_provider_attempt, lease_id, accounting_day, occurred_at,
                     reserved_cost_units, currency, created_at, updated_at)
                SELECT r.analysis_id, a.user_id, r.analysis_id, FALSE, NULL, MIN(r.admission_day), MIN(r.created_at),
                       0, 'USD', MIN(r.created_at), MIN(r.created_at)
                FROM ai_analysis_requests AS r
                JOIN cs_projects AS p ON p.id = r.project_id
                JOIN applications AS a ON a.id = p.app_id
                WHERE r.consumes_quota
                GROUP BY r.analysis_id, a.user_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analysis_budget_entries");
        }
    }
}
