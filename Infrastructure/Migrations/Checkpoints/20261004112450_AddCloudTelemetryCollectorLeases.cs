using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudTelemetryCollectorLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "runtime_collector_lease_owner",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "runtime_collector_lease_until",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "runtime_collector_lease_owner",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "runtime_collector_lease_until",
                table: "deployments");
        }
    }
}
