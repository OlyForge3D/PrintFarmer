using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class FixCameraSnapshotCascade : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CameraSnapshots_Cameras_CameraId",
            table: "CameraSnapshots");

        migrationBuilder.AddForeignKey(
            name: "FK_CameraSnapshots_Cameras_CameraId",
            table: "CameraSnapshots",
            column: "CameraId",
            principalTable: "Cameras",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CameraSnapshots_Cameras_CameraId",
            table: "CameraSnapshots");

        migrationBuilder.AddForeignKey(
            name: "FK_CameraSnapshots_Cameras_CameraId",
            table: "CameraSnapshots",
            column: "CameraId",
            principalTable: "Cameras",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
