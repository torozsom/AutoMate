using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSaaSTelemetryAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cloud_container_app_name",
                table: "deployments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cloud_resource_id",
                table: "deployments",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deployment_daily_telemetry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    container = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sample_count = table.Column<long>(type: "bigint", nullable: false),
                    sum = table.Column<double>(type: "double precision", nullable: false),
                    minimum = table.Column<double>(type: "double precision", nullable: true),
                    maximum = table.Column<double>(type: "double precision", nullable: true),
                    observed_errors = table.Column<long>(type: "bigint", nullable: false),
                    incomplete = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_daily_telemetry", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_daily_telemetry_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_daily_telemetry_deployment_id_day_utc_container_",
                table: "deployment_daily_telemetry",
                columns: new[] { "deployment_id", "day_utc", "container", "metric" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_daily_telemetry_user_id_project_id_day_utc",
                table: "deployment_daily_telemetry",
                columns: new[] { "user_id", "project_id", "day_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_daily_telemetry");

            migrationBuilder.DropColumn(
                name: "cloud_container_app_name",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "cloud_resource_id",
                table: "deployments");
        }
    }
}
