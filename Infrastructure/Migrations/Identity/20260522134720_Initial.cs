#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Initial.
/// </summary>
public partial class Initial : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "data_protection_keys",
            table => new
            {
                id = table.Column<int>("integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                friendly_name = table.Column<string>("text", nullable: true),
                xml = table.Column<string>("text", nullable: true)
            },
            constraints: table => { table.PrimaryKey("pk_data_protection_keys", x => x.id); });

        migrationBuilder.CreateTable(
            "users",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                username = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
                email = table.Column<string>("character varying(255)", maxLength: 255, nullable: false),
                user_type = table.Column<string>("character varying(8)", maxLength: 8, nullable: false),
                account_id = table.Column<string>("text", nullable: true),
                avatar_url = table.Column<string>("text", nullable: true),
                access_token = table.Column<string>("text", nullable: true),
                password_hash = table.Column<string>("text", nullable: true),
                is_email_verified = table.Column<bool>("boolean", nullable: true),
                email_verification_token = table.Column<string>("text", nullable: true),
                verification_token_expiry = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table => { table.PrimaryKey("pk_users", x => x.id); });

        migrationBuilder.CreateTable(
            "applications",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                user_id = table.Column<Guid>("uuid", nullable: false),
                name = table.Column<string>("character varying(200)", maxLength: 200, nullable: false),
                source_type = table.Column<int>("integer", nullable: false),
                source_path_or_url = table.Column<string>("text", nullable: false),
                app_type = table.Column<int>("integer", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_applications", x => x.id);
                table.ForeignKey(
                    "fk_applications_users_user_id",
                    x => x.user_id,
                    "users",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            "cs_projects",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                app_id = table.Column<Guid>("uuid", nullable: false),
                name = table.Column<string>("text", nullable: false),
                path = table.Column<string>("text", nullable: false),
                is_web_project = table.Column<bool>("boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cs_projects", x => x.id);
                table.ForeignKey(
                    "fk_cs_projects_applications_app_id",
                    x => x.app_id,
                    "applications",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            "app_configs",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                cs_project_id = table.Column<Guid>("uuid", nullable: false),
                dot_net_version = table.Column<string>("text", nullable: false),
                local_exposed_port = table.Column<int>("integer", nullable: true),
                requires_db = table.Column<bool>("boolean", nullable: false),
                is_public = table.Column<bool>("boolean", nullable: false),
                environment_variables_json = table.Column<string>("text", nullable: true),
                cloud_azure_region = table.Column<string>("text", nullable: true),
                cloud_registry_name = table.Column<string>("text", nullable: true),
                cloud_resource_group_name = table.Column<string>("text", nullable: true),
                cloud_container_app_name = table.Column<string>("text", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_app_configs", x => x.id);
                table.ForeignKey(
                    "fk_app_configs_cs_projects_cs_project_id",
                    x => x.cs_project_id,
                    "cs_projects",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            "deployments",
            table => new
            {
                id = table.Column<Guid>("uuid", nullable: false),
                cs_project_id = table.Column<Guid>("uuid", nullable: false),
                status = table.Column<int>("integer", nullable: false),
                image_tag = table.Column<string>("text", nullable: true),
                docker_container_id = table.Column<string>("text", nullable: true),
                cloud_git_hub_action_run_id = table.Column<long>("bigint", nullable: true),
                cloud_app_url = table.Column<string>("text", nullable: true),
                cloud_container_revision = table.Column<string>("text", nullable: true),
                created_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_deployments", x => x.id);
                table.ForeignKey(
                    "fk_deployments_cs_projects_cs_project_id",
                    x => x.cs_project_id,
                    "cs_projects",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            "ix_app_configs_cs_project_id",
            "app_configs",
            "cs_project_id",
            unique: true);

        migrationBuilder.CreateIndex(
            "ix_applications_user_id",
            "applications",
            "user_id");

        migrationBuilder.CreateIndex(
            "ix_cs_projects_app_id",
            "cs_projects",
            "app_id");

        migrationBuilder.CreateIndex(
            "ix_deployments_cs_project_id",
            "deployments",
            "cs_project_id");

        migrationBuilder.CreateIndex(
            "ix_users_email",
            "users",
            "email",
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
            "app_configs");

        migrationBuilder.DropTable(
            "data_protection_keys");

        migrationBuilder.DropTable(
            "deployments");

        migrationBuilder.DropTable(
            "cs_projects");

        migrationBuilder.DropTable(
            "applications");

        migrationBuilder.DropTable(
            "users");
    }
}