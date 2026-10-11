using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisRetryScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deployment_analysis_work_items_completed_at_lease_until_cre",
                table: "deployment_analysis_work_items");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "deployment_analysis_work_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "provider_retry_count",
                table: "deployment_analysis_work_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_analysis_work_items_completed_at_next_attempt_at",
                table: "deployment_analysis_work_items",
                columns: new[] { "completed_at", "next_attempt_at", "lease_until", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deployment_analysis_work_items_completed_at_next_attempt_at",
                table: "deployment_analysis_work_items");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "deployment_analysis_work_items");

            migrationBuilder.DropColumn(
                name: "provider_retry_count",
                table: "deployment_analysis_work_items");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_analysis_work_items_completed_at_lease_until_cre",
                table: "deployment_analysis_work_items",
                columns: new[] { "completed_at", "lease_until", "created_at" });
        }
    }
}
