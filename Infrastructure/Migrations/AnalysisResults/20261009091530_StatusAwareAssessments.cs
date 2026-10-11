using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StatusAwareAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_assessment_preferences_json",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assessment_provenance_json",
                table: "ai_deployment_analyses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assessment_sections_json",
                table: "ai_deployment_analyses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_selection_json",
                table: "ai_deployment_analyses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_selection_json",
                table: "ai_analysis_requests",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_assessment_preferences_json",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "assessment_provenance_json",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "assessment_sections_json",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "requested_selection_json",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "requested_selection_json",
                table: "ai_analysis_requests");
        }
    }
}
