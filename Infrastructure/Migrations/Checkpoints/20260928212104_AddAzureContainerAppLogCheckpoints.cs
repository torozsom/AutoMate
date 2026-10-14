#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.Checkpoints;

/// <summary>
///     EF schema migration and target-model metadata for Add Azure Container App Log
///     Checkpoints.
/// </summary>
public partial class AddAzureContainerAppLogCheckpoints : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "azure_container_app_log_checkpoints",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                deployment_id = table.Column<Guid>("uuid", nullable: false),
                source = table.Column<string>("character varying(32)", maxLength: 32, nullable: false),
                last_timestamp = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                last_tie_breaker = table.Column<string>("character varying(128)", maxLength: 128, nullable: true),
                last_successful_query_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_azure_container_app_log_checkpoints", x => x.id);
                table.ForeignKey(
                    "fk_azure_container_app_log_checkpoints_deployments_deployment_",
                    x => x.deployment_id,
                    "deployments",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_azure_container_app_log_checkpoints_deployment_id_source",
            "azure_container_app_log_checkpoints",
            new[] { "deployment_id", "source" },
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
            "azure_container_app_log_checkpoints");
    }
}
