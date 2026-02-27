using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ReviseMaintenanceDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceTasks_MaintenancePlans_MaintenancePlanId",
                table: "MaintenanceTasks");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceTasks_MaintenancePlanId",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "MaintenancePlanId",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "MaintenanceTasks");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "MaintenanceTasks",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresBowdenTube",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresCarbonFilter",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresEnclosure",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresFilamentCutter",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresHeatedBed",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresHeatedChamber",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresHepaFilter",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresLeadScrews",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresLinearRails",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresMultiMaterial",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresPtfeLiner",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresToolchanger",
                table: "MaintenanceTasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceTaskId",
                table: "MaintenanceLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceTaskId",
                table: "MaintenanceAlerts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlanTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaintenancePlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaintenanceTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IntervalHoursOverride = table.Column<double>(type: "float", nullable: true),
                    IntervalDaysOverride = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanTasks_MaintenancePlans_MaintenancePlanId",
                        column: x => x.MaintenancePlanId,
                        principalTable: "MaintenancePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanTasks_MaintenanceTasks_MaintenanceTaskId",
                        column: x => x.MaintenanceTaskId,
                        principalTable: "MaintenanceTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrinterMaintenanceSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaintenancePlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DeployedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterMaintenanceSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterMaintenanceSchedules_MaintenancePlans_MaintenancePlanId",
                        column: x => x.MaintenancePlanId,
                        principalTable: "MaintenancePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTasks_Category",
                table: "MaintenanceTasks",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTasks_TaskName",
                table: "MaintenanceTasks",
                column: "TaskName");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceLogs_MaintenanceTaskId",
                table: "MaintenanceLogs",
                column: "MaintenanceTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceAlerts_MaintenanceTaskId",
                table: "MaintenanceAlerts",
                column: "MaintenanceTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanTasks_MaintenancePlanId_MaintenanceTaskId",
                table: "PlanTasks",
                columns: new[] { "MaintenancePlanId", "MaintenanceTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanTasks_MaintenanceTaskId",
                table: "PlanTasks",
                column: "MaintenanceTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterMaintenanceSchedules_IsActive",
                table: "PrinterMaintenanceSchedules",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId",
                table: "PrinterMaintenanceSchedules",
                columns: new[] { "MaintenancePlanId", "PrinterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterMaintenanceSchedules_PrinterId",
                table: "PrinterMaintenanceSchedules",
                column: "PrinterId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceAlerts_MaintenanceTasks_MaintenanceTaskId",
                table: "MaintenanceAlerts",
                column: "MaintenanceTaskId",
                principalTable: "MaintenanceTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceLogs_MaintenanceTasks_MaintenanceTaskId",
                table: "MaintenanceLogs",
                column: "MaintenanceTaskId",
                principalTable: "MaintenanceTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceAlerts_MaintenanceTasks_MaintenanceTaskId",
                table: "MaintenanceAlerts");

            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceLogs_MaintenanceTasks_MaintenanceTaskId",
                table: "MaintenanceLogs");

            migrationBuilder.DropTable(
                name: "PlanTasks");

            migrationBuilder.DropTable(
                name: "PrinterMaintenanceSchedules");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceTasks_Category",
                table: "MaintenanceTasks");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceTasks_TaskName",
                table: "MaintenanceTasks");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceLogs_MaintenanceTaskId",
                table: "MaintenanceLogs");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceAlerts_MaintenanceTaskId",
                table: "MaintenanceAlerts");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresBowdenTube",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresCarbonFilter",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresEnclosure",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresFilamentCutter",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresHeatedBed",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresHeatedChamber",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresHepaFilter",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresLeadScrews",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresLinearRails",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresMultiMaterial",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresPtfeLiner",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "RequiresToolchanger",
                table: "MaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "MaintenanceTaskId",
                table: "MaintenanceLogs");

            migrationBuilder.DropColumn(
                name: "MaintenanceTaskId",
                table: "MaintenanceAlerts");

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenancePlanId",
                table: "MaintenanceTasks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "MaintenanceTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTasks_MaintenancePlanId",
                table: "MaintenanceTasks",
                column: "MaintenancePlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceTasks_MaintenancePlans_MaintenancePlanId",
                table: "MaintenanceTasks",
                column: "MaintenancePlanId",
                principalTable: "MaintenancePlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
