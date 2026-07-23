using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddCameraSnapshot : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CameraSnapshots_PrintJobs_PrintJobId",
            table: "CameraSnapshots");

        migrationBuilder.CreateIndex(
            name: "IX_CameraSnapshots_CapturedAt",
            table: "CameraSnapshots",
            column: "CapturedAt");

        migrationBuilder.AddForeignKey(
            name: "FK_CameraSnapshots_PrintJobs_PrintJobId",
            table: "CameraSnapshots",
            column: "PrintJobId",
            principalTable: "PrintJobs",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CameraSnapshots_PrintJobs_PrintJobId",
            table: "CameraSnapshots");

        migrationBuilder.DropIndex(
            name: "IX_CameraSnapshots_CapturedAt",
            table: "CameraSnapshots");

        migrationBuilder.AddForeignKey(
            name: "FK_CameraSnapshots_PrintJobs_PrintJobId",
            table: "CameraSnapshots",
            column: "PrintJobId",
            principalTable: "PrintJobs",
            principalColumn: "Id");
    }
}
