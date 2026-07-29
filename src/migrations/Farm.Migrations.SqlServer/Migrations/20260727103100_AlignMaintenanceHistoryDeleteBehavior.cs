using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AlignMaintenanceHistoryDeleteBehavior : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceAlerts_Printers_PrinterId",
            table: "MaintenanceAlerts");

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_Printers_PrinterId",
            table: "MaintenanceLogs");

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceAlerts_Printers_PrinterId",
            table: "MaintenanceAlerts",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_Printers_PrinterId",
            table: "MaintenanceLogs",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceAlerts_Printers_PrinterId",
            table: "MaintenanceAlerts");

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_Printers_PrinterId",
            table: "MaintenanceLogs");

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceAlerts_Printers_PrinterId",
            table: "MaintenanceAlerts",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_Printers_PrinterId",
            table: "MaintenanceLogs",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
