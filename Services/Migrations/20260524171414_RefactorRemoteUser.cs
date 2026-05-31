using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Services.Migrations
{
    /// <inheritdoc />
    public partial class RefactorRemoteUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "access_token",
                table: "users",
                newName: "git_hub_access_token");

            migrationBuilder.AddColumn<string>(
                name: "azure_access_token",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "azure_account_id",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "azure_refresh_token",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "azure_subscription_id",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "azure_tenant_id",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "azure_token_expires_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "azure_access_token",
                table: "users");

            migrationBuilder.DropColumn(
                name: "azure_account_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "azure_refresh_token",
                table: "users");

            migrationBuilder.DropColumn(
                name: "azure_subscription_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "azure_tenant_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "azure_token_expires_at",
                table: "users");

            migrationBuilder.RenameColumn(
                name: "git_hub_access_token",
                table: "users",
                newName: "access_token");
        }
    }
}
