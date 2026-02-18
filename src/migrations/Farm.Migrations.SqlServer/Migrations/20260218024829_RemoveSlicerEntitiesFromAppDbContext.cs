using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSlicerEntitiesFromAppDbContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GcodeFiles_Folders_FolderId",
                table: "GcodeFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_GcodeFileTag_Tags_TagsId",
                table: "GcodeFileTag");

            migrationBuilder.DropForeignKey(
                name: "FK_Model3DTag_Models3D_Model3DId",
                table: "Model3DTag");

            migrationBuilder.DropForeignKey(
                name: "FK_Model3DTag_Tags_TagsId",
                table: "Model3DTag");

            migrationBuilder.DropForeignKey(
                name: "FK_PrinterModelAliases_PrinterModels_PrinterModelId1",
                table: "PrinterModelAliases");

            migrationBuilder.DropForeignKey(
                name: "FK_Printers_MachineProfiles_TemplateMachineProfileId",
                table: "Printers");

            migrationBuilder.DropTable(
                name: "Artifacts");

            migrationBuilder.DropTable(
                name: "FilamentProfiles");

            migrationBuilder.DropTable(
                name: "MachineProfiles");

            migrationBuilder.DropTable(
                name: "Models3D");

            migrationBuilder.DropTable(
                name: "SliceJobs");

            migrationBuilder.DropTable(
                name: "SlicerServices");

            migrationBuilder.DropTable(
                name: "SlicerSettings");

            migrationBuilder.DropTable(
                name: "Workers");

            migrationBuilder.DropTable(
                name: "MachineModelProfiles");

            migrationBuilder.DropTable(
                name: "ProcessProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Printers_TemplateMachineProfileId",
                table: "Printers");

            migrationBuilder.DropIndex(
                name: "IX_PrinterModelAliases_PrinterModelId1",
                table: "PrinterModelAliases");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Tags",
                table: "Tags");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Folders",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PrinterModelId1",
                table: "PrinterModelAliases");

            migrationBuilder.RenameTable(
                name: "Tags",
                newName: "Tag");

            migrationBuilder.RenameTable(
                name: "Folders",
                newName: "FolderNode");

            migrationBuilder.RenameIndex(
                name: "IX_Tags_Name",
                table: "Tag",
                newName: "IX_Tag_Name");

            migrationBuilder.RenameIndex(
                name: "IX_Tags_CreatedAt",
                table: "Tag",
                newName: "IX_Tag_CreatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_Folders_Path_FolderType",
                table: "FolderNode",
                newName: "IX_FolderNode_Path_FolderType");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Tag",
                table: "Tag",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_FolderNode",
                table: "FolderNode",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_GcodeFiles_FolderNode_FolderId",
                table: "GcodeFiles",
                column: "FolderId",
                principalTable: "FolderNode",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_GcodeFileTag_Tag_TagsId",
                table: "GcodeFileTag",
                column: "TagsId",
                principalTable: "Tag",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Model3DTag_Tag_TagsId",
                table: "Model3DTag",
                column: "TagsId",
                principalTable: "Tag",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GcodeFiles_FolderNode_FolderId",
                table: "GcodeFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_GcodeFileTag_Tag_TagsId",
                table: "GcodeFileTag");

            migrationBuilder.DropForeignKey(
                name: "FK_Model3DTag_Tag_TagsId",
                table: "Model3DTag");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Tag",
                table: "Tag");

            migrationBuilder.DropPrimaryKey(
                name: "PK_FolderNode",
                table: "FolderNode");

            migrationBuilder.RenameTable(
                name: "Tag",
                newName: "Tags");

            migrationBuilder.RenameTable(
                name: "FolderNode",
                newName: "Folders");

            migrationBuilder.RenameIndex(
                name: "IX_Tag_Name",
                table: "Tags",
                newName: "IX_Tags_Name");

            migrationBuilder.RenameIndex(
                name: "IX_Tag_CreatedAt",
                table: "Tags",
                newName: "IX_Tags_CreatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_FolderNode_Path_FolderType",
                table: "Folders",
                newName: "IX_Folders_Path_FolderType");

            migrationBuilder.AddColumn<Guid>(
                name: "PrinterModelId1",
                table: "PrinterModelAliases",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Tags",
                table: "Tags",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Folders",
                table: "Folders",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "Artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FilamentProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BedTemperature = table.Column<int>(type: "int", nullable: false),
                    CompatiblePrinters = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Material = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    NozzleTemperature = table.Column<int>(type: "int", nullable: false),
                    PrintSpeed = table.Column<int>(type: "int", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SlicerType = table.Column<int>(type: "int", nullable: false),
                    SlicerVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilamentProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilamentProfiles_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MachineModelProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SlicerType = table.Column<int>(type: "int", nullable: false),
                    SlicerVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineModelProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineModelProfiles_PrinterModels_PrinterModelId",
                        column: x => x.PrinterModelId,
                        principalTable: "PrinterModels",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Models3D",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DimensionX = table.Column<double>(type: "float", nullable: true),
                    DimensionY = table.Column<double>(type: "float", nullable: true),
                    DimensionZ = table.Column<double>(type: "float", nullable: true),
                    FileFormat = table.Column<int>(type: "int", nullable: false),
                    FileHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    HealthStatus = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    IsValid = table.Column<bool>(type: "bit", nullable: false),
                    LastHealthCheckDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastVerificationResult = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    ThumbnailFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    TriangleCount = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidationErrors = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models3D", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models3D_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Models3D_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProcessProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SpecificPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdvancedSettings = table.Column<string>(type: "TEXT", nullable: true),
                    CompatiblePrinters = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EnableSupports = table.Column<bool>(type: "bit", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    InfillPercentage = table.Column<int>(type: "int", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    LayerHeight = table.Column<double>(type: "float", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    PrintSpeed = table.Column<double>(type: "float", nullable: false),
                    Quality = table.Column<int>(type: "int", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SlicerType = table.Column<int>(type: "int", nullable: false),
                    SlicerVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessProfiles_PrinterModels_PrinterModelId",
                        column: x => x.PrinterModelId,
                        principalTable: "PrinterModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProcessProfiles_Printers_SpecificPrinterId",
                        column: x => x.SpecificPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProcessProfiles_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SlicerServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ApiKeyRotatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    LastSeen = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MaxConcurrentJobs = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SlicerType = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UiManifestUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlicerServices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlicerSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    JitterPercent = table.Column<double>(type: "float", nullable: false, defaultValue: 15.0),
                    PerEngineJson = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlicerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActiveJobs = table.Column<int>(type: "int", nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ArtifactBytesProduced = table.Column<long>(type: "bigint", nullable: false),
                    ArtifactsProduced = table.Column<int>(type: "int", nullable: false),
                    AverageProcessingTimeSeconds = table.Column<double>(type: "float", nullable: true),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedJobs = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DisabledReason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    EndpointUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    FailedJobs = table.Column<int>(type: "int", nullable: false),
                    IsDisabled = table.Column<bool>(type: "bit", nullable: false),
                    LastHeartbeat = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OfflineAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OnlineAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RegisteredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ServiceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TotalSlots = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MachineProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MachineModelProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SlicerType = table.Column<int>(type: "int", nullable: false),
                    SlicerVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineProfiles_MachineModelProfiles_MachineModelProfileId",
                        column: x => x.MachineModelProfileId,
                        principalTable: "MachineModelProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MachineProfiles_PrinterModels_PrinterModelId",
                        column: x => x.PrinterModelId,
                        principalTable: "PrinterModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MachineProfiles_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SliceJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SlicerProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ArtifactIdsCsv = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ArtifactsCount = table.Column<int>(type: "int", nullable: true),
                    ArtifactsTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    Checksum = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedPrintTimeSeconds = table.Column<int>(type: "int", nullable: true),
                    FilamentUsedGrams = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModelFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ModelFileUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ProgressMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ProgressPercent = table.Column<int>(type: "int", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequiredCapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultFileUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    SlicerEngine = table.Column<int>(type: "int", nullable: false),
                    SlicerProfileJson = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SliceJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SliceJobs_ProcessProfiles_SlicerProfileId",
                        column: x => x.SlicerProfileId,
                        principalTable: "ProcessProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_TemplateMachineProfileId",
                table: "Printers",
                column: "TemplateMachineProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterModelAliases_PrinterModelId1",
                table: "PrinterModelAliases",
                column: "PrinterModelId1");

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_CreatedAt",
                table: "Artifacts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_JobId",
                table: "Artifacts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_JobId_Kind",
                table: "Artifacts",
                columns: new[] { "JobId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_WorkerId",
                table: "Artifacts",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_CreatedByUserId",
                table: "FilamentProfiles",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_Hash",
                table: "FilamentProfiles",
                column: "Hash",
                unique: true,
                filter: "[Hash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_IsSystem",
                table: "FilamentProfiles",
                column: "IsSystem");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_Material",
                table: "FilamentProfiles",
                column: "Material");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType",
                table: "FilamentProfiles",
                columns: new[] { "Name", "Material", "SlicerType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_SlicerType",
                table: "FilamentProfiles",
                column: "SlicerType");

            migrationBuilder.CreateIndex(
                name: "IX_MachineModelProfiles_PrinterModelId",
                table: "MachineModelProfiles",
                column: "PrinterModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_CreatedByUserId",
                table: "MachineProfiles",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_Hash",
                table: "MachineProfiles",
                column: "Hash",
                unique: true,
                filter: "[Hash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_IsSystem",
                table: "MachineProfiles",
                column: "IsSystem");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_MachineModelProfileId",
                table: "MachineProfiles",
                column: "MachineModelProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_Manufacturer",
                table: "MachineProfiles",
                column: "Manufacturer");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_Name_SlicerType",
                table: "MachineProfiles",
                columns: new[] { "Name", "SlicerType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_PrinterModelId",
                table: "MachineProfiles",
                column: "PrinterModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_SlicerType",
                table: "MachineProfiles",
                column: "SlicerType");

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_FileFormat",
                table: "Models3D",
                column: "FileFormat");

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_FileHash",
                table: "Models3D",
                column: "FileHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_FolderId",
                table: "Models3D",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_HealthStatus",
                table: "Models3D",
                column: "HealthStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_IsValid",
                table: "Models3D",
                column: "IsValid");

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_LastHealthCheckDate",
                table: "Models3D",
                column: "LastHealthCheckDate");

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_UploadedAt",
                table: "Models3D",
                column: "UploadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Models3D_UploadedByUserId",
                table: "Models3D",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_CreatedByUserId",
                table: "ProcessProfiles",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_Hash",
                table: "ProcessProfiles",
                column: "Hash",
                unique: true,
                filter: "[Hash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_IsDefault",
                table: "ProcessProfiles",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_IsPublic",
                table: "ProcessProfiles",
                column: "IsPublic");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_IsSystem",
                table: "ProcessProfiles",
                column: "IsSystem");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_Name_SlicerType_PrinterModelId",
                table: "ProcessProfiles",
                columns: new[] { "Name", "SlicerType", "PrinterModelId" },
                unique: true,
                filter: "[PrinterModelId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_PrinterModelId",
                table: "ProcessProfiles",
                column: "PrinterModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_SlicerType",
                table: "ProcessProfiles",
                column: "SlicerType");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_SpecificPrinterId",
                table: "ProcessProfiles",
                column: "SpecificPrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_PrinterId",
                table: "SliceJobs",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_QueuedAt",
                table: "SliceJobs",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_SlicerProfileId",
                table: "SliceJobs",
                column: "SlicerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_Status",
                table: "SliceJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_Status_Priority_QueuedAt",
                table: "SliceJobs",
                columns: new[] { "Status", "Priority", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_UserId",
                table: "SliceJobs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_WorkerId",
                table: "SliceJobs",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_SlicerServices_Name",
                table: "SlicerServices",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_SlicerServices_SlicerType",
                table: "SlicerServices",
                column: "SlicerType");

            migrationBuilder.CreateIndex(
                name: "IX_SlicerServices_Status",
                table: "SlicerServices",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Workers_LastHeartbeat",
                table: "Workers",
                column: "LastHeartbeat");

            migrationBuilder.CreateIndex(
                name: "IX_Workers_ServiceId",
                table: "Workers",
                column: "ServiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workers_Status",
                table: "Workers",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_GcodeFiles_Folders_FolderId",
                table: "GcodeFiles",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_GcodeFileTag_Tags_TagsId",
                table: "GcodeFileTag",
                column: "TagsId",
                principalTable: "Tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Model3DTag_Models3D_Model3DId",
                table: "Model3DTag",
                column: "Model3DId",
                principalTable: "Models3D",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Model3DTag_Tags_TagsId",
                table: "Model3DTag",
                column: "TagsId",
                principalTable: "Tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PrinterModelAliases_PrinterModels_PrinterModelId1",
                table: "PrinterModelAliases",
                column: "PrinterModelId1",
                principalTable: "PrinterModels",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_MachineProfiles_TemplateMachineProfileId",
                table: "Printers",
                column: "TemplateMachineProfileId",
                principalTable: "MachineProfiles",
                principalColumn: "Id");
        }
    }
}
