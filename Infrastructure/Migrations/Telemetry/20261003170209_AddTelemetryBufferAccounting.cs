#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Telemetry Buffer Accounting.
/// </summary>
public partial class AddTelemetryBufferAccounting : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            "buffered_bytes",
            "telemetry_tenant_states",
            "bigint",
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

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            "buffered_bytes",
            "telemetry_tenant_states");
    }
}