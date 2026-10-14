#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations.RetentionAndPolicy;

/// <summary>
///     EF schema migration and target-model metadata for Preserve Deployment History.
/// </summary>
public partial class PreserveDeploymentHistory : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            "configuration_snapshot_json",
            "deployments",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            "finished_at",
            "deployments",
            "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            "outcome",
            "deployments",
            "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            "resolved_host_port",
            "deployments",
            "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            "retain_until_deleted",
            "ai_deployment_analyses",
            "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            "deployment_archive_cleanups",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                tenant_id = table.Column<Guid>("uuid", nullable: false),
                project_id = table.Column<Guid>("uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table => { table.PrimaryKey("pk_deployment_archive_cleanups", x => x.id); });

        migrationBuilder.CreateIndex(
            "ix_deployment_archive_cleanups_project_id",
            "deployment_archive_cleanups",
            "project_id",
            unique: true);

        // Preserve existing results; execution expiry still bounds pending provider work.
        migrationBuilder.Sql("UPDATE ai_deployment_analyses SET retain_until_deleted = TRUE;");
        // Running is positive completion evidence; a stopped container alone is not proof of successful preparation.
        migrationBuilder.Sql(
            "UPDATE deployments SET outcome = 1 WHERE status = 1; UPDATE deployments SET outcome = 2 WHERE status = 3;");
        // Project cascades from account deletion also enqueue cleanup, without retaining foreign keys to deleted owners.
        migrationBuilder.Sql("""
                             CREATE FUNCTION enqueue_deployment_archive_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
                             BEGIN
                               INSERT INTO deployment_archive_cleanups(id, tenant_id, project_id, created_at, updated_at)
                               VALUES (OLD.id, OLD.user_id, OLD.id, NOW(), NOW()) ON CONFLICT(project_id) DO NOTHING;
                               RETURN OLD;
                             END $$;
                             CREATE TRIGGER project_archive_cleanup BEFORE DELETE ON applications
                             FOR EACH ROW EXECUTE FUNCTION enqueue_deployment_archive_cleanup();
                             """);
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "DROP TRIGGER IF EXISTS project_archive_cleanup ON applications; DROP FUNCTION IF EXISTS enqueue_deployment_archive_cleanup();");
        migrationBuilder.DropTable(
            "deployment_archive_cleanups");

        migrationBuilder.DropColumn(
            "configuration_snapshot_json",
            "deployments");

        migrationBuilder.DropColumn(
            "finished_at",
            "deployments");

        migrationBuilder.DropColumn(
            "outcome",
            "deployments");

        migrationBuilder.DropColumn(
            "resolved_host_port",
            "deployments");

        migrationBuilder.DropColumn(
            "retain_until_deleted",
            "ai_deployment_analyses");
    }
}
