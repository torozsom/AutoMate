#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

/// <summary>
///     EF schema migration and target-model metadata for Refactor Remote User.
/// </summary>
public partial class RefactorRemoteUser : Migration
{
    /// <summary>
    ///     Defines the forward schema and data operations for this migration.
    /// </summary>
    /// <param name="migrationBuilder">Collects operations executed by the EF migration runner.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            "access_token",
            "users",
            "git_hub_access_token");

        migrationBuilder.AddColumn<string>(
            "azure_access_token",
            "users",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "azure_account_id",
            "users",
            "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "azure_refresh_token",
            "users",
            "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "azure_subscription_id",
            "users",
            "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            "azure_tenant_id",
            "users",
            "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            "azure_token_expires_at",
            "users",
            "timestamp with time zone",
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
            "azure_access_token",
            "users");

        migrationBuilder.DropColumn(
            "azure_account_id",
            "users");

        migrationBuilder.DropColumn(
            "azure_refresh_token",
            "users");

        migrationBuilder.DropColumn(
            "azure_subscription_id",
            "users");

        migrationBuilder.DropColumn(
            "azure_tenant_id",
            "users");

        migrationBuilder.DropColumn(
            "azure_token_expires_at",
            "users");

        migrationBuilder.RenameColumn(
            "git_hub_access_token",
            "users",
            "access_token");
    }
}
