using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
/// <remarks>
/// Retained for deployed-history coherence. Existing deployments at any pre-fix HEAD
/// had <c>FK_CameraSnapshots_Cameras_CameraId</c> as <c>ON DELETE CASCADE</c> — one of
/// the two cascade paths from <c>Printers</c> to <c>CameraSnapshots</c> that triggered
/// SQL Server error 1785 at fresh install. This migration converts that FK to
/// <c>Restrict</c> for those installs.
///
/// After the Dallas full-chain adjudication for #953, the baseline
/// <c>20260512193651_AddNozzleDiameterAndHasMmuToPrinter</c> (this provider) already
/// emits the FK as <c>NoAction</c>, so on fresh installs this migration drops-and-recreates
/// the FK in its already-final state (functionally a no-op). It must NOT be removed:
/// skipping it would break the migration chain for deployed installs already carrying
/// its <c>__EFMigrationsHistory</c> row.
/// </remarks>
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
