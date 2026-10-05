using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisQueueLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deployment_analysis_work_items_completed_at_claimed_at",
                table: "deployment_analysis_work_items");

            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "deployment_analysis_work_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                table: "deployment_analysis_work_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until",
                table: "deployment_analysis_work_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_analysis_work_items_completed_at_lease_until_cre",
                table: "deployment_analysis_work_items",
                columns: new[] { "completed_at", "lease_until", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deployment_analysis_work_items_completed_at_lease_until_cre",
                table: "deployment_analysis_work_items");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "deployment_analysis_work_items");

            migrationBuilder.DropColumn(
                name: "lease_id",
                table: "deployment_analysis_work_items");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "deployment_analysis_work_items");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_analysis_work_items_completed_at_claimed_at",
                table: "deployment_analysis_work_items",
                columns: new[] { "completed_at", "claimed_at" });
        }
    }
}
