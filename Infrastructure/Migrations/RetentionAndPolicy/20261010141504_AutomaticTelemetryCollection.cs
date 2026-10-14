#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.RetentionAndPolicy;

/// <summary>
///     EF schema migration and target-model metadata for Automatic Telemetry Collection.
/// </summary>
public partial class AutomaticTelemetryCollection : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing projects adopt automatic collection without rewriting stored diagnostics.
        migrationBuilder.Sql(
            "UPDATE applications SET runtime_diagnostics_enabled = TRUE, managed_telemetry_consent = TRUE;");
        migrationBuilder.AlterColumn<bool>(
            "runtime_diagnostics_enabled",
            "applications",
            "boolean",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "boolean");

        migrationBuilder.AlterColumn<bool>(
            "managed_telemetry_consent",
            "applications",
            "boolean",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "boolean");
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<bool>(
            "runtime_diagnostics_enabled",
            "applications",
            "boolean",
            nullable: false,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: true);

        migrationBuilder.AlterColumn<bool>(
            "managed_telemetry_consent",
            "applications",
            "boolean",
            nullable: false,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: true);
    }
}
