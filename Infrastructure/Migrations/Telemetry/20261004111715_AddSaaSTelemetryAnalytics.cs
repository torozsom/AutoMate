#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.Telemetry;

/// <summary>
///     EF schema migration and target-model metadata for Add Saa STelemetry Analytics.
/// </summary>
public partial class AddSaaSTelemetryAnalytics : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            "cloud_container_app_name",
            "deployments",
            "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "cloud_resource_id",
            "deployments",
            "character varying(1024)",
            maxLength: 1024,
            nullable: true);

        migrationBuilder.CreateTable(
            "deployment_daily_telemetry",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                user_id = table.Column<Guid>("uuid", nullable: false),
                project_id = table.Column<Guid>("uuid", nullable: false),
                deployment_id = table.Column<Guid>("uuid", nullable: false),
                day_utc = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                container = table.Column<string>("character varying(128)", maxLength: 128, nullable: false),
                metric = table.Column<string>("character varying(64)", maxLength: 64, nullable: false),
                unit = table.Column<string>("character varying(32)", maxLength: 32, nullable: false),
                sample_count = table.Column<long>("bigint", nullable: false),
                sum = table.Column<double>("double precision", nullable: false),
                minimum = table.Column<double>("double precision", nullable: true),
                maximum = table.Column<double>("double precision", nullable: true),
                observed_errors = table.Column<long>("bigint", nullable: false),
                incomplete = table.Column<bool>("boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_deployment_daily_telemetry", x => x.id);
                table.ForeignKey(
                    "fk_deployment_daily_telemetry_deployments_deployment_id",
                    x => x.deployment_id,
                    "deployments",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_deployment_daily_telemetry_deployment_id_day_utc_container_",
            "deployment_daily_telemetry",
            ["deployment_id", "day_utc", "container", "metric"],
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_deployment_daily_telemetry_user_id_project_id_day_utc",
            "deployment_daily_telemetry",
            ["user_id", "project_id", "day_utc"]);
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "deployment_daily_telemetry");

        migrationBuilder.DropColumn(
            "cloud_container_app_name",
            "deployments");

        migrationBuilder.DropColumn(
            "cloud_resource_id",
            "deployments");
    }
}
