using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Services.Migrations
{
    /// <inheritdoc />
    public partial class AddDockerDeploymentEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectConfigurations");

            migrationBuilder.AddColumn<string>(
                name: "BuildLogs",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DockerContainerId",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageTag",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LocalProjectConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CsProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DotNetVersion = table.Column<string>(type: "text", nullable: false),
                    ExposedPort = table.Column<int>(type: "integer", nullable: true),
                    RequiresDb = table.Column<bool>(type: "boolean", nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    EnvironmentVariablesJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalProjectConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocalProjectConfigs_CsProjects_CsProjectId",
                        column: x => x.CsProjectId,
                        principalTable: "CsProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalProjectConfigs_CsProjectId",
                table: "LocalProjectConfigs",
                column: "CsProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalProjectConfigs");

            migrationBuilder.DropColumn(
                name: "BuildLogs",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "DockerContainerId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "ImageTag",
                table: "Deployments");

            migrationBuilder.CreateTable(
                name: "ProjectConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CsProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DotNetVersion = table.Column<string>(type: "text", nullable: false),
                    ExposedPort = table.Column<int>(type: "integer", nullable: true),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresDb = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectConfigurations_CsProjects_CsProjectId",
                        column: x => x.CsProjectId,
                        principalTable: "CsProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectConfigurations_CsProjectId",
                table: "ProjectConfigurations",
                column: "CsProjectId",
                unique: true);
        }
    }
}
