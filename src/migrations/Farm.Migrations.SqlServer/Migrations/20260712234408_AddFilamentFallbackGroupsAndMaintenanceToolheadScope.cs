using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddFilamentFallbackGroupsAndMaintenanceToolheadScope : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.AddColumn<Guid>(
            name: "ToolheadId",
            table: "PrinterMaintenanceSchedules",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ToolheadId",
            table: "MaintenanceLogs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ToolheadId",
            table: "MaintenanceAlerts",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "FilamentFallbackGroups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                MaterialType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                DisplayOrder = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentFallbackGroups", x => x.Id);
                table.ForeignKey(
                    name: "FK_FilamentFallbackGroups_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FilamentFallbackGroupMembers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FallbackGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ToolheadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Position = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentFallbackGroupMembers", x => x.Id);
                table.ForeignKey(
                    name: "FK_FilamentFallbackGroupMembers_FilamentFallbackGroups_FallbackGroupId",
                    column: x => x.FallbackGroupId,
                    principalTable: "FilamentFallbackGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_FilamentFallbackGroupMembers_Toolheads_ToolheadId",
                    column: x => x.ToolheadId,
                    principalTable: "Toolheads",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PrinterMaintenanceSchedules_ToolheadId",
            table: "PrinterMaintenanceSchedules",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_NullToolhead",
            table: "PrinterMaintenanceSchedules",
            columns: new[] { "MaintenancePlanId", "PrinterId" },
            unique: true,
            filter: "\"ToolheadId\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_Toolhead",
            table: "PrinterMaintenanceSchedules",
            columns: new[] { "MaintenancePlanId", "PrinterId", "ToolheadId" },
            unique: true,
            filter: "[ToolheadId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_ToolheadId",
            table: "MaintenanceLogs",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_ToolheadId",
            table: "MaintenanceAlerts",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentFallbackGroupMembers_FallbackGroupId",
            table: "FilamentFallbackGroupMembers",
            column: "FallbackGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentFallbackGroupMembers_ToolheadId",
            table: "FilamentFallbackGroupMembers",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroupMembers_GroupId_Position",
            table: "FilamentFallbackGroupMembers",
            columns: new[] { "FallbackGroupId", "Position" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroupMembers_GroupId_ToolheadId",
            table: "FilamentFallbackGroupMembers",
            columns: new[] { "FallbackGroupId", "ToolheadId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FilamentFallbackGroups_PrinterId",
            table: "FilamentFallbackGroups",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroups_PrinterId_Name",
            table: "FilamentFallbackGroups",
            columns: new[] { "PrinterId", "Name" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceAlerts_Toolheads_ToolheadId",
            table: "MaintenanceAlerts",
            column: "ToolheadId",
            principalTable: "Toolheads",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_Toolheads_ToolheadId",
            table: "MaintenanceLogs",
            column: "ToolheadId",
            principalTable: "Toolheads",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Toolheads_ToolheadId",
            table: "PrinterMaintenanceSchedules",
            column: "ToolheadId",
            principalTable: "Toolheads",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM [PrinterMaintenanceSchedules]
            WHERE [Id] IN (
                SELECT [ranked].[Id]
                FROM (
                    SELECT
                        [Id],
                        ROW_NUMBER() OVER (
                            PARTITION BY [MaintenancePlanId], [PrinterId]
                            ORDER BY
                                CASE WHEN [ToolheadId] IS NULL THEN 0 ELSE 1 END,
                                [CreatedAt],
                                [Id]) AS [DuplicateRank]
                    FROM [PrinterMaintenanceSchedules]
                ) AS [ranked]
                WHERE [ranked].[DuplicateRank] > 1
            );
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceAlerts_Toolheads_ToolheadId",
            table: "MaintenanceAlerts");

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_Toolheads_ToolheadId",
            table: "MaintenanceLogs");

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Toolheads_ToolheadId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropTable(
            name: "FilamentFallbackGroupMembers");

        migrationBuilder.DropTable(
            name: "FilamentFallbackGroups");

        migrationBuilder.DropIndex(
            name: "IX_PrinterMaintenanceSchedules_ToolheadId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_NullToolhead",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_Toolhead",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropIndex(
            name: "IX_MaintenanceLogs_ToolheadId",
            table: "MaintenanceLogs");

        migrationBuilder.DropIndex(
            name: "IX_MaintenanceAlerts_ToolheadId",
            table: "MaintenanceAlerts");

        migrationBuilder.DropColumn(
            name: "ToolheadId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropColumn(
            name: "ToolheadId",
            table: "MaintenanceLogs");

        migrationBuilder.DropColumn(
            name: "ToolheadId",
            table: "MaintenanceAlerts");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId",
            table: "PrinterMaintenanceSchedules",
            columns: new[] { "MaintenancePlanId", "PrinterId" },
            unique: true);
    }
}
