using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintablesOAuthToUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrintablesOAuthAccessToken",
                table: "UserSettings",
                type: "nvarchar(max)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrintablesOAuthLinkedAtUtc",
                table: "UserSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrintablesOAuthRefreshToken",
                table: "UserSettings",
                type: "nvarchar(max)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrintablesOAuthScope",
                table: "UserSettings",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrintablesOAuthTokenExpiresAtUtc",
                table: "UserSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrintablesOAuthTokenType",
                table: "UserSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrintablesOAuthAccessToken",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PrintablesOAuthLinkedAtUtc",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PrintablesOAuthRefreshToken",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PrintablesOAuthScope",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PrintablesOAuthTokenExpiresAtUtc",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PrintablesOAuthTokenType",
                table: "UserSettings");
        }
    }
}
