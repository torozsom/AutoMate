#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Add Ai Analysis Result Provenance.
/// </summary>
public partial class AddAiAnalysisResultProvenance : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            "cost_currency",
            "ai_deployment_analyses",
            "character varying(3)",
            maxLength: 3,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            "estimated_cost",
            "ai_deployment_analyses",
            "numeric(18,8)",
            precision: 18,
            scale: 8,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            "input_tokens",
            "ai_deployment_analyses",
            "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "model_version",
            "ai_deployment_analyses",
            "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            "output_tokens",
            "ai_deployment_analyses",
            "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "prompt_version",
            "ai_deployment_analyses",
            "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "requested_model",
            "ai_deployment_analyses",
            "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            "result_schema_version",
            "ai_deployment_analyses",
            "integer",
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
            "cost_currency",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "estimated_cost",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "input_tokens",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "model_version",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "output_tokens",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "prompt_version",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "requested_model",
            "ai_deployment_analyses");

        migrationBuilder.DropColumn(
            "result_schema_version",
            "ai_deployment_analyses");
    }
}