using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterRotationCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastMaintenanceAlertEvaluatedAt",
                table: "PrinterServiceState",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastStatsSyncAttemptedAt",
                table: "PrinterServiceState",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterServiceState_LastMaintenanceAlertEvaluatedAt",
                table: "PrinterServiceState",
                column: "LastMaintenanceAlertEvaluatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterServiceState_LastStatsSyncAttemptedAt",
                table: "PrinterServiceState",
                column: "LastStatsSyncAttemptedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterServiceState_LastMaintenanceAlertEvaluatedAt",
                table: "PrinterServiceState");

            migrationBuilder.DropIndex(
                name: "IX_PrinterServiceState_LastStatsSyncAttemptedAt",
                table: "PrinterServiceState");

            migrationBuilder.DropColumn(
                name: "LastMaintenanceAlertEvaluatedAt",
                table: "PrinterServiceState");

            migrationBuilder.DropColumn(
                name: "LastStatsSyncAttemptedAt",
                table: "PrinterServiceState");
        }
    }
}
