using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Services.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditAndSnakeCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CsProjects_Projects_ProjectId",
                table: "CsProjects");

            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_CsProjects_CsProjectId",
                table: "Deployments");

            migrationBuilder.DropForeignKey(
                name: "FK_LocalProjectConfigs_CsProjects_CsProjectId",
                table: "LocalProjectConfigs");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Users_UserId",
                table: "Projects");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Users",
                table: "Users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Projects",
                table: "Projects");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Deployments",
                table: "Deployments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_LocalProjectConfigs",
                table: "LocalProjectConfigs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DataProtectionKeys",
                table: "DataProtectionKeys");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CsProjects",
                table: "CsProjects");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "users");

            migrationBuilder.RenameTable(
                name: "Projects",
                newName: "projects");

            migrationBuilder.RenameTable(
                name: "Deployments",
                newName: "deployments");

            migrationBuilder.RenameTable(
                name: "LocalProjectConfigs",
                newName: "local_project_configs");

            migrationBuilder.RenameTable(
                name: "DataProtectionKeys",
                newName: "data_protection_keys");

            migrationBuilder.RenameTable(
                name: "CsProjects",
                newName: "cs_projects");

            migrationBuilder.RenameColumn(
                name: "Username",
                table: "users",
                newName: "username");

            migrationBuilder.RenameColumn(
                name: "Email",
                table: "users",
                newName: "email");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "users",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "users",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "UserType",
                table: "users",
                newName: "user_type");

            migrationBuilder.RenameIndex(
                name: "IX_Users_Email",
                table: "users",
                newName: "IX_users_email");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "projects",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "projects",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "projects",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "SourceType",
                table: "projects",
                newName: "source_type");

            migrationBuilder.RenameColumn(
                name: "SourcePathOrUrl",
                table: "projects",
                newName: "source_path_or_url");

            migrationBuilder.RenameColumn(
                name: "AppType",
                table: "projects",
                newName: "app_type");

            migrationBuilder.RenameIndex(
                name: "IX_Projects_UserId",
                table: "projects",
                newName: "ix_projects_user_id");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "deployments",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Logs",
                table: "deployments",
                newName: "logs");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "deployments",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "ImageTag",
                table: "deployments",
                newName: "image_tag");

            migrationBuilder.RenameColumn(
                name: "DockerContainerId",
                table: "deployments",
                newName: "docker_container_id");

            migrationBuilder.RenameColumn(
                name: "CsProjectId",
                table: "deployments",
                newName: "cs_project_id");

            migrationBuilder.RenameColumn(
                name: "BuildLogs",
                table: "deployments",
                newName: "build_logs");

            migrationBuilder.RenameColumn(
                name: "DeployedAt",
                table: "deployments",
                newName: "updated_at");

            migrationBuilder.RenameIndex(
                name: "IX_Deployments_CsProjectId",
                table: "deployments",
                newName: "ix_deployments_cs_project_id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "local_project_configs",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "RequiresDb",
                table: "local_project_configs",
                newName: "requires_db");

            migrationBuilder.RenameColumn(
                name: "IsPublic",
                table: "local_project_configs",
                newName: "is_public");

            migrationBuilder.RenameColumn(
                name: "ExposedPort",
                table: "local_project_configs",
                newName: "exposed_port");

            migrationBuilder.RenameColumn(
                name: "EnvironmentVariablesJson",
                table: "local_project_configs",
                newName: "environment_variables_json");

            migrationBuilder.RenameColumn(
                name: "DotNetVersion",
                table: "local_project_configs",
                newName: "dot_net_version");

            migrationBuilder.RenameColumn(
                name: "CsProjectId",
                table: "local_project_configs",
                newName: "cs_project_id");

            migrationBuilder.RenameIndex(
                name: "IX_LocalProjectConfigs_CsProjectId",
                table: "local_project_configs",
                newName: "ix_local_project_configs_cs_project_id");

            migrationBuilder.RenameColumn(
                name: "Xml",
                table: "data_protection_keys",
                newName: "xml");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "data_protection_keys",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "FriendlyName",
                table: "data_protection_keys",
                newName: "friendly_name");

            migrationBuilder.RenameColumn(
                name: "Path",
                table: "cs_projects",
                newName: "path");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "cs_projects",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "cs_projects",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "ProjectId",
                table: "cs_projects",
                newName: "project_id");

            migrationBuilder.RenameColumn(
                name: "IsWebProject",
                table: "cs_projects",
                newName: "is_web_project");

            migrationBuilder.RenameIndex(
                name: "IX_CsProjects_ProjectId",
                table: "cs_projects",
                newName: "ix_cs_projects_project_id");

            migrationBuilder.AlterColumn<string>(
                name: "username",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "projects",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "projects",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "local_project_configs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "local_project_configs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "cs_projects",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "cs_projects",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddPrimaryKey(
                name: "pk_users",
                table: "users",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_projects",
                table: "projects",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_deployments",
                table: "deployments",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_local_project_configs",
                table: "local_project_configs",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_data_protection_keys",
                table: "data_protection_keys",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_cs_projects",
                table: "cs_projects",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_cs_projects_projects_project_id",
                table: "cs_projects",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_cs_projects_cs_project_id",
                table: "deployments",
                column: "cs_project_id",
                principalTable: "cs_projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_local_project_configs_cs_projects_cs_project_id",
                table: "local_project_configs",
                column: "cs_project_id",
                principalTable: "cs_projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_users_user_id",
                table: "projects",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_cs_projects_projects_project_id",
                table: "cs_projects");

            migrationBuilder.DropForeignKey(
                name: "fk_deployments_cs_projects_cs_project_id",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "fk_local_project_configs_cs_projects_cs_project_id",
                table: "local_project_configs");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_users_user_id",
                table: "projects");

            migrationBuilder.DropPrimaryKey(
                name: "pk_users",
                table: "users");

            migrationBuilder.DropPrimaryKey(
                name: "pk_projects",
                table: "projects");

            migrationBuilder.DropPrimaryKey(
                name: "pk_deployments",
                table: "deployments");

            migrationBuilder.DropPrimaryKey(
                name: "pk_local_project_configs",
                table: "local_project_configs");

            migrationBuilder.DropPrimaryKey(
                name: "pk_data_protection_keys",
                table: "data_protection_keys");

            migrationBuilder.DropPrimaryKey(
                name: "pk_cs_projects",
                table: "cs_projects");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "local_project_configs");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "local_project_configs");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "cs_projects");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "cs_projects");

            migrationBuilder.RenameTable(
                name: "users",
                newName: "Users");

            migrationBuilder.RenameTable(
                name: "projects",
                newName: "Projects");

            migrationBuilder.RenameTable(
                name: "deployments",
                newName: "Deployments");

            migrationBuilder.RenameTable(
                name: "local_project_configs",
                newName: "LocalProjectConfigs");

            migrationBuilder.RenameTable(
                name: "data_protection_keys",
                newName: "DataProtectionKeys");

            migrationBuilder.RenameTable(
                name: "cs_projects",
                newName: "CsProjects");

            migrationBuilder.RenameColumn(
                name: "username",
                table: "Users",
                newName: "Username");

            migrationBuilder.RenameColumn(
                name: "email",
                table: "Users",
                newName: "Email");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "Users",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "Users",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "user_type",
                table: "Users",
                newName: "UserType");

            migrationBuilder.RenameIndex(
                name: "IX_users_email",
                table: "Users",
                newName: "IX_Users_Email");

            migrationBuilder.RenameColumn(
                name: "name",
                table: "Projects",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "Projects",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "Projects",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "source_type",
                table: "Projects",
                newName: "SourceType");

            migrationBuilder.RenameColumn(
                name: "source_path_or_url",
                table: "Projects",
                newName: "SourcePathOrUrl");

            migrationBuilder.RenameColumn(
                name: "app_type",
                table: "Projects",
                newName: "AppType");

            migrationBuilder.RenameIndex(
                name: "ix_projects_user_id",
                table: "Projects",
                newName: "IX_Projects_UserId");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "Deployments",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "logs",
                table: "Deployments",
                newName: "Logs");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "Deployments",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "image_tag",
                table: "Deployments",
                newName: "ImageTag");

            migrationBuilder.RenameColumn(
                name: "docker_container_id",
                table: "Deployments",
                newName: "DockerContainerId");

            migrationBuilder.RenameColumn(
                name: "cs_project_id",
                table: "Deployments",
                newName: "CsProjectId");

            migrationBuilder.RenameColumn(
                name: "build_logs",
                table: "Deployments",
                newName: "BuildLogs");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "Deployments",
                newName: "DeployedAt");

            migrationBuilder.RenameIndex(
                name: "ix_deployments_cs_project_id",
                table: "Deployments",
                newName: "IX_Deployments_CsProjectId");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "LocalProjectConfigs",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "requires_db",
                table: "LocalProjectConfigs",
                newName: "RequiresDb");

            migrationBuilder.RenameColumn(
                name: "is_public",
                table: "LocalProjectConfigs",
                newName: "IsPublic");

            migrationBuilder.RenameColumn(
                name: "exposed_port",
                table: "LocalProjectConfigs",
                newName: "ExposedPort");

            migrationBuilder.RenameColumn(
                name: "environment_variables_json",
                table: "LocalProjectConfigs",
                newName: "EnvironmentVariablesJson");

            migrationBuilder.RenameColumn(
                name: "dot_net_version",
                table: "LocalProjectConfigs",
                newName: "DotNetVersion");

            migrationBuilder.RenameColumn(
                name: "cs_project_id",
                table: "LocalProjectConfigs",
                newName: "CsProjectId");

            migrationBuilder.RenameIndex(
                name: "ix_local_project_configs_cs_project_id",
                table: "LocalProjectConfigs",
                newName: "IX_LocalProjectConfigs_CsProjectId");

            migrationBuilder.RenameColumn(
                name: "xml",
                table: "DataProtectionKeys",
                newName: "Xml");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "DataProtectionKeys",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "friendly_name",
                table: "DataProtectionKeys",
                newName: "FriendlyName");

            migrationBuilder.RenameColumn(
                name: "path",
                table: "CsProjects",
                newName: "Path");

            migrationBuilder.RenameColumn(
                name: "name",
                table: "CsProjects",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "CsProjects",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "project_id",
                table: "CsProjects",
                newName: "ProjectId");

            migrationBuilder.RenameColumn(
                name: "is_web_project",
                table: "CsProjects",
                newName: "IsWebProject");

            migrationBuilder.RenameIndex(
                name: "ix_cs_projects_project_id",
                table: "CsProjects",
                newName: "IX_CsProjects_ProjectId");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Projects",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Users",
                table: "Users",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Projects",
                table: "Projects",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Deployments",
                table: "Deployments",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LocalProjectConfigs",
                table: "LocalProjectConfigs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DataProtectionKeys",
                table: "DataProtectionKeys",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CsProjects",
                table: "CsProjects",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CsProjects_Projects_ProjectId",
                table: "CsProjects",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Deployments_CsProjects_CsProjectId",
                table: "Deployments",
                column: "CsProjectId",
                principalTable: "CsProjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LocalProjectConfigs_CsProjects_CsProjectId",
                table: "LocalProjectConfigs",
                column: "CsProjectId",
                principalTable: "CsProjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Users_UserId",
                table: "Projects",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
