using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Services.Migrations
{
    /// <inheritdoc />
    public partial class AddCsProjectsRefactoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_Projects_ProjectId",
                table: "Deployments");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectConfigurations_Projects_ProjectId",
                table: "ProjectConfigurations");

            migrationBuilder.RenameColumn(
                name: "ProjectId",
                table: "ProjectConfigurations",
                newName: "CsProjectId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectConfigurations_ProjectId",
                table: "ProjectConfigurations",
                newName: "IX_ProjectConfigurations_CsProjectId");

            migrationBuilder.RenameColumn(
                name: "ProjectId",
                table: "Deployments",
                newName: "CsProjectId");

            migrationBuilder.RenameIndex(
                name: "IX_Deployments_ProjectId",
                table: "Deployments",
                newName: "IX_Deployments_CsProjectId");

            migrationBuilder.CreateTable(
                name: "CsProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    IsWebProject = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CsProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CsProjects_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CsProjects_ProjectId",
                table: "CsProjects",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Deployments_CsProjects_CsProjectId",
                table: "Deployments",
                column: "CsProjectId",
                principalTable: "CsProjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectConfigurations_CsProjects_CsProjectId",
                table: "ProjectConfigurations",
                column: "CsProjectId",
                principalTable: "CsProjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_CsProjects_CsProjectId",
                table: "Deployments");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectConfigurations_CsProjects_CsProjectId",
                table: "ProjectConfigurations");

            migrationBuilder.DropTable(
                name: "CsProjects");

            migrationBuilder.RenameColumn(
                name: "CsProjectId",
                table: "ProjectConfigurations",
                newName: "ProjectId");

            migrationBuilder.RenameIndex(
                name: "IX_ProjectConfigurations_CsProjectId",
                table: "ProjectConfigurations",
                newName: "IX_ProjectConfigurations_ProjectId");

            migrationBuilder.RenameColumn(
                name: "CsProjectId",
                table: "Deployments",
                newName: "ProjectId");

            migrationBuilder.RenameIndex(
                name: "IX_Deployments_CsProjectId",
                table: "Deployments",
                newName: "IX_Deployments_ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Deployments_Projects_ProjectId",
                table: "Deployments",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectConfigurations_Projects_ProjectId",
                table: "ProjectConfigurations",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
