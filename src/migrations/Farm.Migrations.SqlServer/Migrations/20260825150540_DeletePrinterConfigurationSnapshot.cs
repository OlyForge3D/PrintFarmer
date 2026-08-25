using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
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
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PrinterConfigurationSnapshotId",
                table: "CalibrationAttempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PrinterConfigurationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Backend = table.Column<int>(type: "int", nullable: false),
                    BackendApiVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BackendVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CapturedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExactFilamentProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExactMachineProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExactProcessProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilamentProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FilamentProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FirmwareDetectionSource = table.Column<int>(type: "int", nullable: false),
                    FirmwareFamily = table.Column<int>(type: "int", nullable: false),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    GcodeDialect = table.Column<int>(type: "int", nullable: false),
                    MachineProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MachineProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PrinterConfigurationRevision = table.Column<long>(type: "bigint", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProcessProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SanitizedSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SlicerContainerDigest = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SlicerDistribution = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SlicerEngine = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SlicerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SnapshotSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
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
