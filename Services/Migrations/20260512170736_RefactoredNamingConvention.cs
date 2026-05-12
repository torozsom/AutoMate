using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Services.Migrations
{
    /// <inheritdoc />
    public partial class RefactoredNamingConvention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VerificationTokenExpiry",
                table: "users",
                newName: "verification_token_expiry");

            migrationBuilder.RenameColumn(
                name: "PasswordHash",
                table: "users",
                newName: "password_hash");

            migrationBuilder.RenameColumn(
                name: "IsEmailVerified",
                table: "users",
                newName: "is_email_verified");

            migrationBuilder.RenameColumn(
                name: "EmailVerificationToken",
                table: "users",
                newName: "email_verification_token");

            migrationBuilder.RenameColumn(
                name: "AvatarUrl",
                table: "users",
                newName: "avatar_url");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "users",
                newName: "account_id");

            migrationBuilder.RenameColumn(
                name: "AccessToken",
                table: "users",
                newName: "access_token");

            migrationBuilder.RenameIndex(
                name: "IX_users_email",
                table: "users",
                newName: "ix_users_email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "verification_token_expiry",
                table: "users",
                newName: "VerificationTokenExpiry");

            migrationBuilder.RenameColumn(
                name: "password_hash",
                table: "users",
                newName: "PasswordHash");

            migrationBuilder.RenameColumn(
                name: "is_email_verified",
                table: "users",
                newName: "IsEmailVerified");

            migrationBuilder.RenameColumn(
                name: "email_verification_token",
                table: "users",
                newName: "EmailVerificationToken");

            migrationBuilder.RenameColumn(
                name: "avatar_url",
                table: "users",
                newName: "AvatarUrl");

            migrationBuilder.RenameColumn(
                name: "account_id",
                table: "users",
                newName: "AccountId");

            migrationBuilder.RenameColumn(
                name: "access_token",
                table: "users",
                newName: "AccessToken");

            migrationBuilder.RenameIndex(
                name: "ix_users_email",
                table: "users",
                newName: "IX_users_email");
        }
    }
}
