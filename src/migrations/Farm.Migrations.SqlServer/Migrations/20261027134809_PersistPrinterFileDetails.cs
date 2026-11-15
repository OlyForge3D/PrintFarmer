using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
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
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "PrinterFileLocations",
                type: "nvarchar(max)",
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
