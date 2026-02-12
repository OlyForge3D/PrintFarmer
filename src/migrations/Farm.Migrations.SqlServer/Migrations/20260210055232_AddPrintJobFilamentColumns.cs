using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintJobFilamentColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilamentColor",
                table: "PrintJobs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilamentName",
                table: "PrintJobs",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilamentVendor",
                table: "PrintJobs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "PrintJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectName",
                table: "PrintJobs",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpoolmanFilamentId",
                table: "PrintJobs",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilamentColor",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "FilamentName",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "FilamentVendor",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "ProjectName",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "SpoolmanFilamentId",
                table: "PrintJobs");
        }
    }
}
