using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class RestrictMaintenanceLogResolvedAlertDelete : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            principalTable: "MaintenanceAlerts",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            principalTable: "MaintenanceAlerts",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }
}
