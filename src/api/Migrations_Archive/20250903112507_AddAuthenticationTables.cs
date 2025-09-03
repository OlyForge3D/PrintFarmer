using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Api.api.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthenticationTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Actions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Manufacturers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Manufacturers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ResourceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    IsSystemRole = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpoolmanConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpoolmanConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmailConfirmationToken = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    PasswordResetToken = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    PasswordResetExpires = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastLogin = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MaxX = table.Column<double>(type: "REAL", nullable: true),
                    MaxY = table.Column<double>(type: "REAL", nullable: true),
                    MaxZ = table.Column<double>(type: "REAL", nullable: true),
                    DefaultBackend = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_Manufacturers_ManufacturerId",
                        column: x => x.ManufacturerId,
                        principalTable: "Manufacturers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Granted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Printers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ServerUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OriginalServerUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Backend = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DateAcquired = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Printers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Printers_Manufacturers_ManufacturerId",
                        column: x => x.ManufacturerId,
                        principalTable: "Manufacturers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Printers_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

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
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OriginalPrinterPath = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenOnPrinter = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RequiredNozzleDiameter = table.Column<double>(type: "REAL", nullable: true),
                    RequiredMaterial = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CompatibleMaterials = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedPrintTimeMinutes = table.Column<double>(type: "REAL", nullable: true),
                    EstimatedFilamentLengthMm = table.Column<double>(type: "REAL", nullable: true),
                    EstimatedFilamentWeightG = table.Column<double>(type: "REAL", nullable: true),
                    RequiredBuildVolumeX = table.Column<double>(type: "REAL", nullable: true),
                    RequiredBuildVolumeY = table.Column<double>(type: "REAL", nullable: true),
                    RequiredBuildVolumeZ = table.Column<double>(type: "REAL", nullable: true),
                    TargetPrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SlicerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SlicerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SlicerSettings = table.Column<string>(type: "TEXT", nullable: true),
                    ThumbnailPath = table.Column<string>(type: "TEXT", nullable: true),
                    LayerHeight = table.Column<double>(type: "REAL", nullable: true),
                    InfillPercentage = table.Column<double>(type: "REAL", nullable: true),
                    PrintTemperatures = table.Column<string>(type: "TEXT", nullable: true),
                    BedTemperature = table.Column<double>(type: "REAL", nullable: true),
                    PrintSpeed = table.Column<double>(type: "REAL", nullable: true),
                    TargetPrinterModels = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GcodeFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GcodeFiles_Models_TargetModelId",
                        column: x => x.TargetModelId,
                        principalTable: "Models",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GcodeFiles_Printers_SourcePrinterId",
                        column: x => x.SourcePrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GcodeFiles_Printers_TargetPrinterId",
                        column: x => x.TargetPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id");
                });

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
                        principalColumn: "Id");
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
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SupportsAutoLeveling = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxPrintSpeed = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                name: "Spools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Material = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WeightGrams = table.Column<double>(type: "REAL", nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    InUse = table.Column<bool>(type: "INTEGER", nullable: false),
                    AssignedPrinterId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Spools_Printers_AssignedPrinterId",
                        column: x => x.AssignedPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedPrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    QueuePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiredNozzleDiameter = table.Column<decimal>(type: "TEXT", nullable: true),
                    RequiredMaterialType = table.Column<string>(type: "TEXT", nullable: true),
                    RequiredCapabilities = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedPrintTime = table.Column<long>(type: "INTEGER", nullable: true),
                    EstimatedFilamentUsage = table.Column<double>(type: "REAL", nullable: true),
                    ActualStartTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActualEndTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActualPrintTime = table.Column<long>(type: "INTEGER", nullable: true),
                    ActualFilamentUsage = table.Column<double>(type: "REAL", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredPrinterIds = table.Column<string>(type: "TEXT", nullable: true),
                    ExcludedPrinterIds = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintJobs_GcodeFiles_GcodeFileId",
                        column: x => x.GcodeFileId,
                        principalTable: "GcodeFiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PrintJobs_Printers_AssignedPrinterId",
                        column: x => x.AssignedPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id");
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
                name: "IX_Actions_Name",
                table: "Actions",
                column: "Name",
                unique: true);

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
                name: "IX_GcodeFiles_SourcePrinterId",
                table: "GcodeFiles",
                column: "SourcePrinterId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Manufacturers_Name",
                table: "Manufacturers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_ManufacturerId_Name",
                table: "Models",
                columns: new[] { "ManufacturerId", "Name" },
                unique: true);

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
                name: "IX_Printers_ManufacturerId",
                table: "Printers",
                column: "ManufacturerId");

            migrationBuilder.CreateIndex(
                name: "IX_Printers_ModelId",
                table: "Printers",
                column: "ModelId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Resources_IsActive",
                table: "Resources",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_Name",
                table: "Resources",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ResourceType",
                table: "Resources",
                column: "ResourceType");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_ActionId",
                table: "RolePermissions",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_ResourceId",
                table: "RolePermissions",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleId_ResourceId_ActionId",
                table: "RolePermissions",
                columns: new[] { "RoleId", "ResourceId", "ActionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_IsActive",
                table: "Roles",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_IsSystemRole",
                table: "Roles",
                column: "IsSystemRole");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Spools_AssignedPrinterId",
                table: "Spools",
                column: "AssignedPrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_ExpiresAt",
                table: "UserRoles",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_IsActive",
                table: "UserRoles",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_CreatedAt",
                table: "Users",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsActive",
                table: "Users",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveredGcodeFiles");

            migrationBuilder.DropTable(
                name: "PrinterCapabilities");

            migrationBuilder.DropTable(
                name: "PrintJobs");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "SpoolmanConfigs");

            migrationBuilder.DropTable(
                name: "Spools");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "GcodeHarvestOperations");

            migrationBuilder.DropTable(
                name: "GcodeFiles");

            migrationBuilder.DropTable(
                name: "Actions");

            migrationBuilder.DropTable(
                name: "Resources");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Printers");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "Manufacturers");
        }
    }
}
