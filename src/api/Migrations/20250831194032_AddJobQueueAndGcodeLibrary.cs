using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddJobQueueAndGcodeLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BedTemperature",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "SpoolId",
                table: "PrintJobs");

            migrationBuilder.RenameColumn(
                name: "StartedAt",
                table: "PrintJobs",
                newName: "RequiredNozzleDiameter");

            migrationBuilder.RenameColumn(
                name: "ProgressPercentage",
                table: "PrintJobs",
                newName: "EstimatedFilamentUsage");

            migrationBuilder.RenameColumn(
                name: "HotendTemperature",
                table: "PrintJobs",
                newName: "ActualFilamentUsage");

            migrationBuilder.RenameColumn(
                name: "ErrorMessage",
                table: "PrintJobs",
                newName: "RequiredMaterialType");

            migrationBuilder.RenameColumn(
                name: "CurrentState",
                table: "PrintJobs",
                newName: "FailureReason");

            migrationBuilder.RenameColumn(
                name: "CompletedAt",
                table: "PrintJobs",
                newName: "EstimatedPrintTime");

            migrationBuilder.RenameColumn(
                name: "AutoAssign",
                table: "PrintJobs",
                newName: "QueuePosition");

            migrationBuilder.AddColumn<DateTime>(
                name: "ActualEndTime",
                table: "PrintJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "ActualPrintTime",
                table: "PrintJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActualStartTime",
                table: "PrintJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PrintJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "PrintJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PrinterCapabilities",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "MaxPrintSpeed",
                table: "PrinterCapabilities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsAutoLeveling",
                table: "PrinterCapabilities",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "PrinterCapabilities",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<double>(
                name: "BedTemperature",
                table: "GcodeFiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "GcodeFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<double>(
                name: "InfillPercentage",
                table: "GcodeFiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenOnPrinter",
                table: "GcodeFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LayerHeight",
                table: "GcodeFiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalPrinterPath",
                table: "GcodeFiles",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PrintSpeed",
                table: "GcodeFiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrintTemperatures",
                table: "GcodeFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "GcodeFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SourcePrinterId",
                table: "GcodeFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPrinterModels",
                table: "GcodeFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "GcodeFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "GcodeHarvestOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    FilesFound = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesErrored = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytesProcessed = table.Column<long>(type: "INTEGER", nullable: false),
                    IncludeSubdirectories = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxFileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedAfter = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GcodeHarvestOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GcodeHarvestOperations_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveredGcodeFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HarvestOperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrinterPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FileHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsSelected = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlreadyInLibrary = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExistingLibraryFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProcessingFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractedSlicerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExtractedSlicerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExtractedPrintTime = table.Column<double>(type: "REAL", nullable: true),
                    ExtractedFilamentLength = table.Column<double>(type: "REAL", nullable: true),
                    ExtractedNozzleDiameter = table.Column<double>(type: "REAL", nullable: true),
                    ExtractedMaterial = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExtractedLayerHeight = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ExtractedInfill = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredGcodeFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveredGcodeFiles_GcodeHarvestOperations_HarvestOperationId",
                        column: x => x.HarvestOperationId,
                        principalTable: "GcodeHarvestOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_Source",
                table: "GcodeFiles",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_SourcePrinterId",
                table: "GcodeFiles",
                column: "SourcePrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredGcodeFiles_AlreadyInLibrary",
                table: "DiscoveredGcodeFiles",
                column: "AlreadyInLibrary");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredGcodeFiles_FileHash",
                table: "DiscoveredGcodeFiles",
                column: "FileHash");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredGcodeFiles_HarvestOperationId",
                table: "DiscoveredGcodeFiles",
                column: "HarvestOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredGcodeFiles_IsSelected",
                table: "DiscoveredGcodeFiles",
                column: "IsSelected");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeHarvestOperations_PrinterId",
                table: "GcodeHarvestOperations",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeHarvestOperations_StartedAt",
                table: "GcodeHarvestOperations",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeHarvestOperations_Status",
                table: "GcodeHarvestOperations",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_GcodeFiles_Printers_SourcePrinterId",
                table: "GcodeFiles",
                column: "SourcePrinterId",
                principalTable: "Printers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GcodeFiles_Printers_SourcePrinterId",
                table: "GcodeFiles");

            migrationBuilder.DropTable(
                name: "DiscoveredGcodeFiles");

            migrationBuilder.DropTable(
                name: "GcodeHarvestOperations");

            migrationBuilder.DropIndex(
                name: "IX_GcodeFiles_Source",
                table: "GcodeFiles");

            migrationBuilder.DropIndex(
                name: "IX_GcodeFiles_SourcePrinterId",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "ActualEndTime",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "ActualPrintTime",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "ActualStartTime",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PrinterCapabilities");

            migrationBuilder.DropColumn(
                name: "MaxPrintSpeed",
                table: "PrinterCapabilities");

            migrationBuilder.DropColumn(
                name: "SupportsAutoLeveling",
                table: "PrinterCapabilities");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "PrinterCapabilities");

            migrationBuilder.DropColumn(
                name: "BedTemperature",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "InfillPercentage",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "LastSeenOnPrinter",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "LayerHeight",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "OriginalPrinterPath",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "PrintSpeed",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "PrintTemperatures",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "SourcePrinterId",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "TargetPrinterModels",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "GcodeFiles");

            migrationBuilder.RenameColumn(
                name: "RequiredNozzleDiameter",
                table: "PrintJobs",
                newName: "StartedAt");

            migrationBuilder.RenameColumn(
                name: "RequiredMaterialType",
                table: "PrintJobs",
                newName: "ErrorMessage");

            migrationBuilder.RenameColumn(
                name: "QueuePosition",
                table: "PrintJobs",
                newName: "AutoAssign");

            migrationBuilder.RenameColumn(
                name: "FailureReason",
                table: "PrintJobs",
                newName: "CurrentState");

            migrationBuilder.RenameColumn(
                name: "EstimatedPrintTime",
                table: "PrintJobs",
                newName: "CompletedAt");

            migrationBuilder.RenameColumn(
                name: "EstimatedFilamentUsage",
                table: "PrintJobs",
                newName: "ProgressPercentage");

            migrationBuilder.RenameColumn(
                name: "ActualFilamentUsage",
                table: "PrintJobs",
                newName: "HotendTemperature");

            migrationBuilder.AddColumn<double>(
                name: "BedTemperature",
                table: "PrintJobs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpoolId",
                table: "PrintJobs",
                type: "INTEGER",
                nullable: true);
        }
    }
}
