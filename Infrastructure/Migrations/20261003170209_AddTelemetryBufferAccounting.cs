using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryBufferAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "buffered_bytes",
                table: "telemetry_tenant_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
            // Reconcile any short-lived buffers created by the preceding rollout migration.
            migrationBuilder.Sql("""
                UPDATE telemetry_tenant_states AS state
                SET buffered_bytes = COALESCE((SELECT sum(delivery_bytes)
                    FROM deployment_diagnostic_records AS record
                    WHERE record.tenant_id = state.tenant_id AND record.delivery_json IS NOT NULL), 0);
                INSERT INTO telemetry_tenant_states
                    (id, tenant_id, due_at, attempts, last_stored_at, dropped_events,
                     rate_window_start, rate_window_bytes, buffered_bytes, metric_identities_json, created_at, updated_at)
                VALUES (gen_random_uuid(), '00000000-0000-0000-0000-000000000000', now(), 0, now(), 0, now(), 0,
                    COALESCE((SELECT sum(delivery_bytes) FROM deployment_diagnostic_records WHERE delivery_json IS NOT NULL), 0),
                    '{}', now(), now())
                ON CONFLICT (tenant_id) DO UPDATE SET buffered_bytes = EXCLUDED.buffered_bytes;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "buffered_bytes",
                table: "telemetry_tenant_states");
        }
    }
}
