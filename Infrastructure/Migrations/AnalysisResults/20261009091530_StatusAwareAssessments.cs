#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Status Aware Assessments.
/// </summary>
public partial class StatusAwareAssessments : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            "ai_assessment_preferences_json",
            "deployments",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "assessment_provenance_json",
            "ai_deployment_analyses",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "assessment_sections_json",
            "ai_deployment_analyses",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "requested_selection_json",
            "ai_deployment_analyses",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "requested_selection_json",
            "ai_analysis_requests",
            "text",
            nullable: true);
    }

    /// <summary>
    ///     Defines downgrade operations for this migration without promising restoration of removed
    ///     data.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            "ai_assessment_preferences_json",
            "deployments");

        migrationBuilder.DropColumn(
            "assessment_provenance_json",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "assessment_sections_json",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "requested_selection_json",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "requested_selection_json",
            "ai_analysis_requests");
    }
}
