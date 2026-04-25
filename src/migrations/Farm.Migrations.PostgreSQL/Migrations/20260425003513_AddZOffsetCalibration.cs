using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddZOffsetCalibration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "LastZOffsetCalibrationAt",
            table: "Printers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "ZOffsetMm",
            table: "Printers",
            type: "numeric",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LastZOffsetCalibrationAt",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "ZOffsetMm",
            table: "Printers");
    }
}
