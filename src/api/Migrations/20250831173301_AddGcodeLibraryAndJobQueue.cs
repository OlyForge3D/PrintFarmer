using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGcodeLibraryAndJobQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Models_Manufacturers_ManufacturerId",
                table: "Models");

            migrationBuilder.AlterColumn<double>(
                name: "WeightGrams",
                table: "Spools",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "float");

            migrationBuilder.AlterColumn<string>(
                name: "Material",
                table: "Spools",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<bool>(
                name: "InUse",
                table: "Spools",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "bit");

            migrationBuilder.AlterColumn<string>(
                name: "ColorHex",
                table: "Spools",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<Guid>(
                name: "AssignedPrinterId",
                table: "Spools",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Spools",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<string>(
                name: "BaseUrl",
                table: "SpoolmanConfigs",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "SpoolmanConfigs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int")
                .Annotation("Sqlite:Autoincrement", true)
                .OldAnnotation("Sqlite:Autoincrement", true);

            migrationBuilder.AlterColumn<string>(
                name: "ServerUrl",
                table: "Printers",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "OriginalServerUrl",
                table: "Printers",
                type: "TEXT",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Notes",
                table: "Printers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Printers",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<Guid>(
                name: "ModelId",
                table: "Printers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ManufacturerId",
                table: "Printers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "Printers",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "DateAcquired",
                table: "Printers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Backend",
                table: "Printers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "ApiKey",
                table: "Printers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Printers",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Models",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<double>(
                name: "MaxZ",
                table: "Models",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "MaxY",
                table: "Models",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "MaxX",
                table: "Models",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ManufacturerId",
                table: "Models",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<int>(
                name: "DefaultBackend",
                table: "Models",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Models",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Manufacturers",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Manufacturers",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateTable(
                name: "GcodeFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    RequiredNozzleDiameter = table.Column<double>(type: "REAL", nullable: true),
                    RequiredMaterial = table.Column<string>(type: "TEXT", nullable: true),
                    CompatibleMaterials = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedPrintTimeMinutes = table.Column<double>(type: "REAL", nullable: true),
                    EstimatedFilamentLengthMm = table.Column<double>(type: "REAL", nullable: true),
                    EstimatedFilamentWeightG = table.Column<double>(type: "REAL", nullable: true),
                    RequiredBuildVolumeX = table.Column<double>(type: "REAL", nullable: true),
                    RequiredBuildVolumeY = table.Column<double>(type: "REAL", nullable: true),
                    RequiredBuildVolumeZ = table.Column<double>(type: "REAL", nullable: true),
                    TargetPrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SlicerName = table.Column<string>(type: "TEXT", nullable: true),
                    SlicerVersion = table.Column<string>(type: "TEXT", nullable: true),
                    SlicerSettings = table.Column<string>(type: "TEXT", nullable: true),
                    ThumbnailPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GcodeFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GcodeFiles_Models_TargetModelId",
                        column: x => x.TargetModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GcodeFiles_Printers_TargetPrinterId",
                        column: x => x.TargetPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PrinterCapabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NozzleDiameter = table.Column<double>(type: "REAL", nullable: true),
                    SupportedMaterials = table.Column<string>(type: "TEXT", nullable: true),
                    MaxBuildVolumeX = table.Column<double>(type: "REAL", nullable: true),
                    MaxBuildVolumeY = table.Column<double>(type: "REAL", nullable: true),
                    MaxBuildVolumeZ = table.Column<double>(type: "REAL", nullable: true),
                    HasHeatedBed = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasEnclosure = table.Column<bool>(type: "INTEGER", nullable: false),
                    MultiMaterial = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberOfExtruders = table.Column<int>(type: "INTEGER", nullable: false),
                    MinHotendTemp = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxHotendTemp = table.Column<int>(type: "INTEGER", nullable: true),
                    MinBedTemp = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxBedTemp = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentMaterial = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentSpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterCapabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterCapabilities_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedPrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HotendTemperature = table.Column<double>(type: "REAL", nullable: true),
                    BedTemperature = table.Column<double>(type: "REAL", nullable: true),
                    SpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProgressPercentage = table.Column<double>(type: "REAL", nullable: true),
                    CurrentState = table.Column<string>(type: "TEXT", nullable: true),
                    RequiredCapabilities = table.Column<string>(type: "TEXT", nullable: true),
                    AutoAssign = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferredPrinterIds = table.Column<string>(type: "TEXT", nullable: true),
                    ExcludedPrinterIds = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintJobs_GcodeFiles_GcodeFileId",
                        column: x => x.GcodeFileId,
                        principalTable: "GcodeFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrintJobs_Printers_AssignedPrinterId",
                        column: x => x.AssignedPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_FileHash",
                table: "GcodeFiles",
                column: "FileHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_RequiredMaterial",
                table: "GcodeFiles",
                column: "RequiredMaterial");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_RequiredNozzleDiameter",
                table: "GcodeFiles",
                column: "RequiredNozzleDiameter");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_TargetModelId",
                table: "GcodeFiles",
                column: "TargetModelId");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_TargetPrinterId",
                table: "GcodeFiles",
                column: "TargetPrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_UploadedAt",
                table: "GcodeFiles",
                column: "UploadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterCapabilities_IsAvailable",
                table: "PrinterCapabilities",
                column: "IsAvailable");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterCapabilities_NozzleDiameter",
                table: "PrinterCapabilities",
                column: "NozzleDiameter");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterCapabilities_PrinterId",
                table: "PrinterCapabilities",
                column: "PrinterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_AssignedPrinterId",
                table: "PrintJobs",
                column: "AssignedPrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_GcodeFileId",
                table: "PrintJobs",
                column: "GcodeFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_Priority",
                table: "PrintJobs",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_QueuedAt",
                table: "PrintJobs",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_Status",
                table: "PrintJobs",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_Models_Manufacturers_ManufacturerId",
                table: "Models",
                column: "ManufacturerId",
                principalTable: "Manufacturers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Models_Manufacturers_ManufacturerId",
                table: "Models");

            migrationBuilder.DropTable(
                name: "PrinterCapabilities");

            migrationBuilder.DropTable(
                name: "PrintJobs");

            migrationBuilder.DropTable(
                name: "GcodeFiles");

            migrationBuilder.AlterColumn<double>(
                name: "WeightGrams",
                table: "Spools",
                type: "float",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<string>(
                name: "Material",
                table: "Spools",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<bool>(
                name: "InUse",
                table: "Spools",
                type: "bit",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "ColorHex",
                table: "Spools",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<Guid>(
                name: "AssignedPrinterId",
                table: "Spools",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Spools",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "BaseUrl",
                table: "SpoolmanConfigs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "SpoolmanConfigs",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .Annotation("Sqlite:Autoincrement", true)
                .OldAnnotation("Sqlite:Autoincrement", true);

            migrationBuilder.AlterColumn<string>(
                name: "ServerUrl",
                table: "Printers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "OriginalServerUrl",
                table: "Printers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Notes",
                table: "Printers",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Printers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<Guid>(
                name: "ModelId",
                table: "Printers",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ManufacturerId",
                table: "Printers",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "Printers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "DateAcquired",
                table: "Printers",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Backend",
                table: "Printers",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "ApiKey",
                table: "Printers",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Printers",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Models",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<double>(
                name: "MaxZ",
                table: "Models",
                type: "float",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "MaxY",
                table: "Models",
                type: "float",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "MaxX",
                table: "Models",
                type: "float",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ManufacturerId",
                table: "Models",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "DefaultBackend",
                table: "Models",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Models",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Manufacturers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Manufacturers",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddForeignKey(
                name: "FK_Models_Manufacturers_ManufacturerId",
                table: "Models",
                column: "ManufacturerId",
                principalTable: "Manufacturers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
