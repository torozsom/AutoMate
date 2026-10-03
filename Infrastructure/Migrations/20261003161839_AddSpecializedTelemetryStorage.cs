using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecializedTelemetryStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "buffer_expires_at",
                table: "deployment_diagnostic_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "delivery_accepted",
                table: "deployment_diagnostic_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "delivery_bytes",
                table: "deployment_diagnostic_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "delivery_json",
                table: "deployment_diagnostic_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metric_samples_json",
                table: "deployment_diagnostic_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_identity_json",
                table: "deployment_diagnostic_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stored_at",
                table: "deployment_diagnostic_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "deployment_diagnostic_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "managed_telemetry_consent",
                table: "applications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "runtime_diagnostics_enabled",
                table: "applications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "telemetry_tenant_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dropped_events = table.Column<long>(type: "bigint", nullable: false),
                    rate_window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rate_window_bytes = table.Column<long>(type: "bigint", nullable: false),
                    metric_identities_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_tenant_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnostic_records_buffer_expires_at",
                table: "deployment_diagnostic_records",
                column: "buffer_expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnostic_records_tenant_id_order_id",
                table: "deployment_diagnostic_records",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_tenant_states_due_at",
                table: "telemetry_tenant_states",
                column: "due_at");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_tenant_states_tenant_id",
                table: "telemetry_tenant_states",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "telemetry_tenant_states");

            migrationBuilder.DropIndex(
                name: "ix_deployment_diagnostic_records_buffer_expires_at",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropIndex(
                name: "ix_deployment_diagnostic_records_tenant_id_order_id",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "buffer_expires_at",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "delivery_accepted",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "delivery_bytes",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "delivery_json",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "metric_samples_json",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "source_identity_json",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "stored_at",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "deployment_diagnostic_records");

            migrationBuilder.DropColumn(
                name: "managed_telemetry_consent",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "runtime_diagnostics_enabled",
                table: "applications");
        }
    }
}
