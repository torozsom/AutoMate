using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAnalysisResultProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cost_currency",
                table: "ai_deployment_analyses",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_cost",
                table: "ai_deployment_analyses",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "input_tokens",
                table: "ai_deployment_analyses",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_version",
                table: "ai_deployment_analyses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "output_tokens",
                table: "ai_deployment_analyses",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_version",
                table: "ai_deployment_analyses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_model",
                table: "ai_deployment_analyses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "result_schema_version",
                table: "ai_deployment_analyses",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_currency",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "estimated_cost",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "input_tokens",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "model_version",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "output_tokens",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "prompt_version",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "requested_model",
                table: "ai_deployment_analyses");

            migrationBuilder.DropColumn(
                name: "result_schema_version",
                table: "ai_deployment_analyses");
        }
    }
}
