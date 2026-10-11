using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFailedDeploymentAnalysisEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "failed_deployment_analysis_events",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_failed_deployment_analysis_events", x => x.deployment_id);
                    table.ForeignKey(
                        name: "fk_failed_deployment_analysis_events_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_failed_deployment_analysis_events_completed_at_created_at",
                table: "failed_deployment_analysis_events",
                columns: new[] { "completed_at", "created_at" });

            migrationBuilder.Sql(FailedDeploymentAnalysisTrigger.CreateSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FailedDeploymentAnalysisTrigger.DropSql);

            migrationBuilder.DropTable(
                name: "failed_deployment_analysis_events");
        }
    }
}
