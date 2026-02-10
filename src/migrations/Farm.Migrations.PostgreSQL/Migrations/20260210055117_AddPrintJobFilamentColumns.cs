using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
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
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilamentName",
                table: "PrintJobs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilamentVendor",
                table: "PrintJobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "PrintJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectName",
                table: "PrintJobs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpoolmanFilamentId",
                table: "PrintJobs",
                type: "integer",
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
