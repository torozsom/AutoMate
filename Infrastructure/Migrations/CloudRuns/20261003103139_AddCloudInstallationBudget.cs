#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Cloud Installation Budget.
/// </summary>
public partial class AddCloudInstallationBudget : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "cloud_installation_budgets",
            table => new
            {
                installation_id = table.Column<long>("bigint", nullable: false),
                paused_until = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                throttle_count = table.Column<int>("integer", nullable: false)
            },
            constraints: table => { table.PrimaryKey("pk_cloud_installation_budgets", x => x.installation_id); });
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "cloud_installation_budgets");
    }
}
