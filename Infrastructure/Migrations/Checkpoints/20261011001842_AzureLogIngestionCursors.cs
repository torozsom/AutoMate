using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AzureLogIngestionCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_ingestion_tie_breaker",
                table: "azure_container_app_log_checkpoints",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_ingestion_timestamp",
                table: "azure_container_app_log_checkpoints",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_ingestion_tie_breaker",
                table: "azure_container_app_log_checkpoints");

            migrationBuilder.DropColumn(
                name: "last_ingestion_timestamp",
                table: "azure_container_app_log_checkpoints");
        }
    }
}
