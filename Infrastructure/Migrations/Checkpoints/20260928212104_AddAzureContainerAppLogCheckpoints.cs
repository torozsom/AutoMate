using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAzureContainerAppLogCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "azure_container_app_log_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_tie_breaker = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_successful_query_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_azure_container_app_log_checkpoints", x => x.id);
                    table.ForeignKey(
                        name: "fk_azure_container_app_log_checkpoints_deployments_deployment_",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_azure_container_app_log_checkpoints_deployment_id_source",
                table: "azure_container_app_log_checkpoints",
                columns: new[] { "deployment_id", "source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "azure_container_app_log_checkpoints");
        }
    }
}
