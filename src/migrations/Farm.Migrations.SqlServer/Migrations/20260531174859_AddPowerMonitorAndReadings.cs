using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddPowerMonitorAndReadings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<DateTime>(
            name: "Timestamp",
            table: "LoginAuditEntries",
            type: "datetime2",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset");

        migrationBuilder.CreateTable(
            name: "PowerMonitors",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProviderType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                DeviceAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                ElectricityRateUsdPerKwh = table.Column<decimal>(type: "decimal(10,4)", precision: 10, scale: 4, nullable: false),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PowerMonitors", x => x.Id);
                table.ForeignKey(
                    name: "FK_PowerMonitors_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PowerReadings",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                PowerMonitorId = table.Column<int>(type: "int", nullable: false),
                WattsNow = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                KwhTotal = table.Column<decimal>(type: "decimal(14,4)", precision: 14, scale: 4, nullable: true),
                RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PowerReadings", x => x.Id);
                table.ForeignKey(
                    name: "FK_PowerReadings_PowerMonitors_PowerMonitorId",
                    column: x => x.PowerMonitorId,
                    principalTable: "PowerMonitors",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PowerMonitors_PrinterId",
            table: "PowerMonitors",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PowerReadings_PowerMonitorId",
            table: "PowerReadings",
            column: "PowerMonitorId");

        migrationBuilder.CreateIndex(
            name: "IX_PowerReadings_RecordedAt",
            table: "PowerReadings",
            column: "RecordedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PowerReadings");

        migrationBuilder.DropTable(
            name: "PowerMonitors");

        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "Timestamp",
            table: "LoginAuditEntries",
            type: "datetimeoffset",
            nullable: false,
            oldClrType: typeof(DateTime),
            oldType: "datetime2");
    }
}
