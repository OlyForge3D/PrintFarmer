using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PersistPrinterFileDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DetailsFetchedAtUtc",
                table: "PrinterFileLocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "PrinterFileLocations",
                type: "character varying(32768)",
                maxLength: 32768,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetailsFetchedAtUtc",
                table: "PrinterFileLocations");

            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "PrinterFileLocations");
        }
    }
}
