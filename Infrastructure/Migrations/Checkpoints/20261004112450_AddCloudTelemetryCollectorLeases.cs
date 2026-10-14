#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.Checkpoints;

/// <summary>
///     EF schema migration and target-model metadata for Add Cloud Telemetry Collector Leases.
/// </summary>
public partial class AddCloudTelemetryCollectorLeases : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            "runtime_collector_lease_owner",
            "deployments",
            "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            "runtime_collector_lease_until",
            "deployments",
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
            "runtime_collector_lease_owner",
            "deployments");

        migrationBuilder.DropColumn(
            "runtime_collector_lease_until",
            "deployments");
    }
}
