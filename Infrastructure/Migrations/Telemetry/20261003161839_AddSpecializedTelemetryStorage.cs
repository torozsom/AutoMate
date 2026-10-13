#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Specialized Telemetry Storage.
/// </summary>
public partial class AddSpecializedTelemetryStorage : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            "buffer_expires_at",
            "deployment_diagnostic_records",
            "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            "delivery_accepted",
            "deployment_diagnostic_records",
            "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            "delivery_bytes",
            "deployment_diagnostic_records",
            "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            "delivery_json",
            "deployment_diagnostic_records",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "metric_samples_json",
            "deployment_diagnostic_records",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "source_identity_json",
            "deployment_diagnostic_records",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            "stored_at",
            "deployment_diagnostic_records",
            "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            "tenant_id",
            "deployment_diagnostic_records",
            "uuid",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            "managed_telemetry_consent",
            "applications",
            "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            "runtime_diagnostics_enabled",
            "applications",
            "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            "telemetry_tenant_states",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                tenant_id = table.Column<Guid>("uuid", nullable: false),
                lease_id = table.Column<Guid>("uuid", nullable: true),
                lease_until = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                due_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                attempts = table.Column<int>("integer", nullable: false),
                last_stored_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                dropped_events = table.Column<long>("bigint", nullable: false),
                rate_window_start = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                rate_window_bytes = table.Column<long>("bigint", nullable: false),
                metric_identities_json = table.Column<string>("text", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table => { table.PrimaryKey("pk_telemetry_tenant_states", x => x.id); });

        migrationBuilder.CreateIndex(
            "ix_deployment_diagnostic_records_buffer_expires_at",
            "deployment_diagnostic_records",
            "buffer_expires_at");

        migrationBuilder.CreateIndex(
            "ix_deployment_diagnostic_records_tenant_id_order_id",
            "deployment_diagnostic_records",
            new[] { "tenant_id", "order_id" });

        migrationBuilder.CreateIndex(
            "ix_telemetry_tenant_states_due_at",
            "telemetry_tenant_states",
            "due_at");

        migrationBuilder.CreateIndex(
            "ix_telemetry_tenant_states_tenant_id",
            "telemetry_tenant_states",
            "tenant_id",
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
            "telemetry_tenant_states");

        migrationBuilder.DropIndex(
            "ix_deployment_diagnostic_records_buffer_expires_at",
            "deployment_diagnostic_records");

        migrationBuilder.DropIndex(
            "ix_deployment_diagnostic_records_tenant_id_order_id",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "buffer_expires_at",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "delivery_accepted",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "delivery_bytes",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "delivery_json",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "metric_samples_json",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "source_identity_json",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "stored_at",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "tenant_id",
            "deployment_diagnostic_records");

        migrationBuilder.DropColumn(
            "managed_telemetry_consent",
            "applications");

        migrationBuilder.DropColumn(
            "runtime_diagnostics_enabled",
            "applications");
    }
}
