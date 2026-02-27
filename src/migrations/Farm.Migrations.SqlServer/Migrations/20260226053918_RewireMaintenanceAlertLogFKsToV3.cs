using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RewireMaintenanceAlertLogFKsToV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceAlerts_MaintenanceSchedules_MaintenanceScheduleId",
                table: "MaintenanceAlerts");

            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceLogs_MaintenanceSchedules_MaintenanceScheduleId",
                table: "MaintenanceLogs");

            migrationBuilder.DropTable(
                name: "MaintenanceSchedules");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceAlerts_MaintenanceScheduleId",
                table: "MaintenanceAlerts");

            migrationBuilder.DropColumn(
                name: "MaintenanceScheduleId",
                table: "MaintenanceAlerts");

            migrationBuilder.RenameColumn(
                name: "MaintenanceScheduleId",
                table: "MaintenanceLogs",
                newName: "PrinterMaintenanceScheduleId");

            migrationBuilder.RenameIndex(
                name: "IX_MaintenanceLogs_MaintenanceScheduleId",
                table: "MaintenanceLogs",
                newName: "IX_MaintenanceLogs_PrinterMaintenanceScheduleId");

            migrationBuilder.AddColumn<Guid>(
                name: "PrinterMaintenanceScheduleId",
                table: "MaintenanceAlerts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceAlerts_PrinterMaintenanceScheduleId",
                table: "MaintenanceAlerts",
                column: "PrinterMaintenanceScheduleId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceAlerts_PrinterMaintenanceSchedules_PrinterMaintenanceScheduleId",
                table: "MaintenanceAlerts",
                column: "PrinterMaintenanceScheduleId",
                principalTable: "PrinterMaintenanceSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceLogs_PrinterMaintenanceSchedules_PrinterMaintenanceScheduleId",
                table: "MaintenanceLogs",
                column: "PrinterMaintenanceScheduleId",
                principalTable: "PrinterMaintenanceSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceAlerts_PrinterMaintenanceSchedules_PrinterMaintenanceScheduleId",
                table: "MaintenanceAlerts");

            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceLogs_PrinterMaintenanceSchedules_PrinterMaintenanceScheduleId",
                table: "MaintenanceLogs");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceAlerts_PrinterMaintenanceScheduleId",
                table: "MaintenanceAlerts");

            migrationBuilder.DropColumn(
                name: "PrinterMaintenanceScheduleId",
                table: "MaintenanceAlerts");

            migrationBuilder.RenameColumn(
                name: "PrinterMaintenanceScheduleId",
                table: "MaintenanceLogs",
                newName: "MaintenanceScheduleId");

            migrationBuilder.RenameIndex(
                name: "IX_MaintenanceLogs_PrinterMaintenanceScheduleId",
                table: "MaintenanceLogs",
                newName: "IX_MaintenanceLogs_MaintenanceScheduleId");

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceScheduleId",
                table: "MaintenanceAlerts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.CreateTable(
                name: "MaintenanceSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Component = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    EstimatedDurationMinutes = table.Column<int>(type: "int", nullable: true),
                    IntervalDays = table.Column<int>(type: "int", nullable: true),
                    IntervalHours = table.Column<double>(type: "float", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MotionType = table.Column<int>(type: "int", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    TaskName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceSchedules_PrinterModels_PrinterModelId",
                        column: x => x.PrinterModelId,
                        principalTable: "PrinterModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaintenanceSchedules_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceAlerts_MaintenanceScheduleId",
                table: "MaintenanceAlerts",
                column: "MaintenanceScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_IsActive_IsDefault",
                table: "MaintenanceSchedules",
                columns: new[] { "IsActive", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_PrinterId",
                table: "MaintenanceSchedules",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_PrinterModelId",
                table: "MaintenanceSchedules",
                column: "PrinterModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceAlerts_MaintenanceSchedules_MaintenanceScheduleId",
                table: "MaintenanceAlerts",
                column: "MaintenanceScheduleId",
                principalTable: "MaintenanceSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceLogs_MaintenanceSchedules_MaintenanceScheduleId",
                table: "MaintenanceLogs",
                column: "MaintenanceScheduleId",
                principalTable: "MaintenanceSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
