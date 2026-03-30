using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPerToolheadFilamentTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentFilamentColor",
                table: "Toolheads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentMaterial",
                table: "Toolheads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentSpoolId",
                table: "Toolheads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ToolheadType",
                table: "Toolheads",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExtruderCount",
                table: "GcodeFiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilamentPerExtruderLengthMm",
                table: "GcodeFiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilamentPerExtruderWeightG",
                table: "GcodeFiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PrintJobToolheadUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolheadIndex = table.Column<int>(type: "int", nullable: false),
                    SpoolmanSpoolId = table.Column<int>(type: "int", nullable: true),
                    FilamentUsageGrams = table.Column<double>(type: "float", nullable: true),
                    FilamentName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    FilamentColor = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    MaterialCostUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobToolheadUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintJobToolheadUsages_PrintJobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Toolheads_CurrentSpoolId",
                table: "Toolheads",
                column: "CurrentSpoolId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobToolheadUsages_PrintJobId_ToolheadIndex",
                table: "PrintJobToolheadUsages",
                columns: new[] { "PrintJobId", "ToolheadIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintJobToolheadUsages");

            migrationBuilder.DropIndex(
                name: "IX_Toolheads_CurrentSpoolId",
                table: "Toolheads");

            migrationBuilder.DropColumn(
                name: "CurrentFilamentColor",
                table: "Toolheads");

            migrationBuilder.DropColumn(
                name: "CurrentMaterial",
                table: "Toolheads");

            migrationBuilder.DropColumn(
                name: "CurrentSpoolId",
                table: "Toolheads");

            migrationBuilder.DropColumn(
                name: "ToolheadType",
                table: "Toolheads");

            migrationBuilder.DropColumn(
                name: "ExtruderCount",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "FilamentPerExtruderLengthMm",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "FilamentPerExtruderWeightG",
                table: "GcodeFiles");
        }
    }
}
