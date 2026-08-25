using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDeprecatedCalibrationPrinterColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalibrationFilamentProfileId",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CalibrationHardwareVerifiedAtUtc",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CalibrationMachineProfileId",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CalibrationMotionType",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CalibrationProcessProfileId",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CalibrationProfileFormat",
                table: "Printers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CalibrationFilamentProfileId",
                table: "Printers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CalibrationHardwareVerifiedAtUtc",
                table: "Printers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CalibrationMachineProfileId",
                table: "Printers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CalibrationMotionType",
                table: "Printers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CalibrationProcessProfileId",
                table: "Printers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalibrationProfileFormat",
                table: "Printers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
