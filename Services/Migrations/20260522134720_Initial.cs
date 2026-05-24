using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Services.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    user_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    account_id = table.Column<string>(type: "text", nullable: true),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    access_token = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    is_email_verified = table.Column<bool>(type: "boolean", nullable: true),
                    email_verification_token = table.Column<string>(type: "text", nullable: true),
                    verification_token_expiry = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_type = table.Column<int>(type: "integer", nullable: false),
                    source_path_or_url = table.Column<string>(type: "text", nullable: false),
                    app_type = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_applications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cs_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    is_web_project = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cs_projects", x => x.id);
                    table.ForeignKey(
                        name: "fk_cs_projects_applications_app_id",
                        column: x => x.app_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cs_project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dot_net_version = table.Column<string>(type: "text", nullable: false),
                    local_exposed_port = table.Column<int>(type: "integer", nullable: true),
                    requires_db = table.Column<bool>(type: "boolean", nullable: false),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    environment_variables_json = table.Column<string>(type: "text", nullable: true),
                    cloud_azure_region = table.Column<string>(type: "text", nullable: true),
                    cloud_registry_name = table.Column<string>(type: "text", nullable: true),
                    cloud_resource_group_name = table.Column<string>(type: "text", nullable: true),
                    cloud_container_app_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_configs", x => x.id);
                    table.ForeignKey(
                        name: "fk_app_configs_cs_projects_cs_project_id",
                        column: x => x.cs_project_id,
                        principalTable: "cs_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cs_project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    image_tag = table.Column<string>(type: "text", nullable: true),
                    docker_container_id = table.Column<string>(type: "text", nullable: true),
                    cloud_git_hub_action_run_id = table.Column<long>(type: "bigint", nullable: true),
                    cloud_app_url = table.Column<string>(type: "text", nullable: true),
                    cloud_container_revision = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployments_cs_projects_cs_project_id",
                        column: x => x.cs_project_id,
                        principalTable: "cs_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_configs_cs_project_id",
                table: "app_configs",
                column: "cs_project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_applications_user_id",
                table: "applications",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cs_projects_app_id",
                table: "cs_projects",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_cs_project_id",
                table: "deployments",
                column: "cs_project_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_configs");

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "cs_projects");

            migrationBuilder.DropTable(
                name: "applications");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
