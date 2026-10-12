#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Azure Log Ingestion Cursors.
/// </summary>
public partial class AzureLogIngestionCursors : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            "last_ingestion_tie_breaker",
            "azure_container_app_log_checkpoints",
            "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            "last_ingestion_timestamp",
            "azure_container_app_log_checkpoints",
            "timestamp with time zone",
            nullable: true);
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            "last_ingestion_tie_breaker",
            "azure_container_app_log_checkpoints");

        migrationBuilder.DropColumn(
            "last_ingestion_timestamp",
            "azure_container_app_log_checkpoints");
    }
}