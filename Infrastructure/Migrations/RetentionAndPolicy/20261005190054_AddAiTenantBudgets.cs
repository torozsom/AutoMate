#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.RetentionAndPolicy;

/// <summary>
///     EF schema migration and target-model metadata for Add Ai Tenant Budgets.
/// </summary>
public partial class AddAiTenantBudgets : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "ai_analysis_budget_entries",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                tenant_id = table.Column<Guid>("uuid", nullable: false),
                analysis_id = table.Column<Guid>("uuid", nullable: false),
                is_provider_attempt = table.Column<bool>("boolean", nullable: false),
                lease_id = table.Column<Guid>("uuid", nullable: true),
                accounting_day = table.Column<DateOnly>("date", nullable: false),
                occurred_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                reserved_cost_units = table.Column<long>("bigint", nullable: false),
                currency = table.Column<string>("character varying(3)", maxLength: 3, nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ai_analysis_budget_entries", x => x.id);
                table.ForeignKey(
                    "fk_ai_analysis_budget_entries_users_tenant_id",
                    x => x.tenant_id,
                    "users",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_ai_analysis_budget_entries_lease_id",
            "ai_analysis_budget_entries",
            "lease_id",
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_ai_analysis_budget_entries_occurred_at",
            "ai_analysis_budget_entries",
            "occurred_at");

        migrationBuilder.CreateIndex(
            "ix_ai_analysis_budget_entries_tenant_id_accounting_day_is_prov",
            "ai_analysis_budget_entries",
            new[] { "tenant_id", "accounting_day", "is_provider_attempt" });

        migrationBuilder.CreateIndex(
            "ix_ai_analysis_budget_entries_tenant_id_is_provider_attempt_oc",
            "ai_analysis_budget_entries",
            new[] { "tenant_id", "is_provider_attempt", "occurred_at" });

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

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "ai_analysis_budget_entries");
    }
}
