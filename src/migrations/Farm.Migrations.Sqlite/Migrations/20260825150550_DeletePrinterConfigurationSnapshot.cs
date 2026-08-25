using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <summary>
    /// Drops the <c>PrinterConfigurationSnapshots</c> table and both FK columns that
    /// referenced it (#1989 / D3b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ OPERATOR WARNING — IRREVERSIBLE DATA LOSS: this migration's <see cref="Down"/>
    /// recreates the table/columns as empty schema shells only. It cannot restore any rows
    /// that existed before <see cref="Up"/> ran. Rolling this migration back does not undo
    /// the data loss.
    /// </para>
    /// <para>
    /// Any <c>CalibrationProject</c>/<c>CalibrationAttempt</c> row created before Path D's
    /// snapshot-linkage removal (#1981/D4) may have carried a non-null snapshot FK pointing
    /// at real historical printer-configuration data. That historical linkage — and the
    /// snapshot rows it pointed at — is permanently discarded by this migration. Operators
    /// who need to preserve that history for audit purposes must export the
    /// <c>PrinterConfigurationSnapshots</c> table (and the FK values on the two referencing
    /// tables) before applying this migration to production.
    /// </para>
    /// </remarks>
    public partial class DeletePrinterConfigurationSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalibrationAttempts_PrinterConfigurationSnapshots_PrinterConfigurationSnapshotId",
                table: "CalibrationAttempts");

            migrationBuilder.DropTable(
                name: "PrinterConfigurationSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_CalibrationAttempts_PrinterConfigurationSnapshotId",
                table: "CalibrationAttempts");

            migrationBuilder.DropColumn(
                name: "CurrentPrinterConfigurationSnapshotId",
                table: "CalibrationProjects");

            migrationBuilder.DropColumn(
                name: "PrinterConfigurationSnapshotId",
                table: "CalibrationAttempts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentPrinterConfigurationSnapshotId",
                table: "CalibrationProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PrinterConfigurationSnapshotId",
                table: "CalibrationAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PrinterConfigurationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Backend = table.Column<int>(type: "INTEGER", nullable: false),
                    BackendApiVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BackendVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CapturedBySubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExactFilamentProfileJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExactMachineProfileJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExactProcessProfileJson = table.Column<string>(type: "TEXT", nullable: true),
                    FilamentProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FilamentProfileSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FirmwareDetectionSource = table.Column<int>(type: "INTEGER", nullable: false),
                    FirmwareFamily = table.Column<int>(type: "INTEGER", nullable: false),
                    FirmwareVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    GcodeDialect = table.Column<int>(type: "INTEGER", nullable: false),
                    MachineProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MachineProfileSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PrinterConfigurationRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProcessProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProcessProfileSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SanitizedSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SlicerContainerDigest = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SlicerDistribution = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SlicerEngine = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SlicerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SnapshotSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterConfigurationSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterConfigurationSnapshots_CalibrationProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "CalibrationProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationAttempts_PrinterConfigurationSnapshotId",
                table: "CalibrationAttempts",
                column: "PrinterConfigurationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterConfigurationSnapshots_AttemptId",
                table: "PrinterConfigurationSnapshots",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterConfigurationSnapshots_ProjectId_SnapshotSha256",
                table: "PrinterConfigurationSnapshots",
                columns: new[] { "ProjectId", "SnapshotSha256" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CalibrationAttempts_PrinterConfigurationSnapshots_PrinterConfigurationSnapshotId",
                table: "CalibrationAttempts",
                column: "PrinterConfigurationSnapshotId",
                principalTable: "PrinterConfigurationSnapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
