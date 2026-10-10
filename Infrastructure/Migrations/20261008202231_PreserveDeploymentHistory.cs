using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreserveDeploymentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "configuration_snapshot_json",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finished_at",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "outcome",
                table: "deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "resolved_host_port",
                table: "deployments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "retain_until_deleted",
                table: "ai_deployment_analyses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "deployment_archive_cleanups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_archive_cleanups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_archive_cleanups_project_id",
                table: "deployment_archive_cleanups",
                column: "project_id",
                unique: true);

            // Preserve existing results; execution expiry still bounds pending provider work.
            migrationBuilder.Sql("UPDATE ai_deployment_analyses SET retain_until_deleted = TRUE;");
            // Running is positive completion evidence; a stopped container alone is not proof of successful preparation.
            migrationBuilder.Sql("UPDATE deployments SET outcome = 1 WHERE status = 1; UPDATE deployments SET outcome = 2 WHERE status = 3;");
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS project_archive_cleanup ON applications; DROP FUNCTION IF EXISTS enqueue_deployment_archive_cleanup();");
            migrationBuilder.DropTable(
                name: "deployment_archive_cleanups");

            migrationBuilder.DropColumn(
                name: "configuration_snapshot_json",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "finished_at",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "resolved_host_port",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "retain_until_deleted",
                table: "ai_deployment_analyses");
        }
    }
}
