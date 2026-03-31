using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class InitialV1 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "slicer");

        migrationBuilder.CreateTable(
            name: "Artifacts",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                FileName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                RelativePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Artifacts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FilamentProfiles",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                Material = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Manufacturer = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                SlicerType = table.Column<int>(type: "int", nullable: false),
                NozzleTemperature = table.Column<int>(type: "int", nullable: false),
                BedTemperature = table.Column<int>(type: "int", nullable: false),
                PrintSpeed = table.Column<int>(type: "int", nullable: false),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CompatiblePrinters = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                IsDefault = table.Column<bool>(type: "bit", nullable: false),
                IsPublic = table.Column<bool>(type: "bit", nullable: false),
                SlicerVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MachineModelProfiles",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Manufacturer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                SlicerType = table.Column<int>(type: "int", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                IsPublic = table.Column<bool>(type: "bit", nullable: false),
                SlicerVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MachineModelProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Models3D",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FileFormat = table.Column<int>(type: "int", nullable: false),
                DimensionX = table.Column<double>(type: "float", nullable: true),
                DimensionY = table.Column<double>(type: "float", nullable: true),
                DimensionZ = table.Column<double>(type: "float", nullable: true),
                TriangleCount = table.Column<int>(type: "int", nullable: true),
                IsValid = table.Column<bool>(type: "bit", nullable: false),
                ValidationErrors = table.Column<string>(type: "TEXT", nullable: true),
                UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FilePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                ThumbnailFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                FileHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastHealthCheckDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                HealthStatus = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                LastVerificationResult = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Models3D", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProcessProfiles",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                SlicerType = table.Column<int>(type: "int", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SpecificPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LayerHeight = table.Column<double>(type: "float", nullable: false),
                InfillPercentage = table.Column<int>(type: "int", nullable: false),
                PrintSpeed = table.Column<double>(type: "float", nullable: false),
                EnableSupports = table.Column<bool>(type: "bit", nullable: false),
                Quality = table.Column<int>(type: "int", nullable: false),
                AdvancedSettings = table.Column<string>(type: "TEXT", nullable: true),
                SlicerVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CompatiblePrinters = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IsDefault = table.Column<bool>(type: "bit", nullable: false),
                IsPublic = table.Column<bool>(type: "bit", nullable: false),
                IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProcessProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SlicerServices",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                SlicerType = table.Column<int>(type: "int", nullable: false),
                Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                Host = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                UiManifestUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                MaxConcurrentJobs = table.Column<int>(type: "int", nullable: false),
                Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                LastSeen = table.Column<DateTime>(type: "datetime2", nullable: false),
                ApiKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ApiKeyRotatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Tags = table.Column<string>(type: "nvarchar(max)", nullable: true),
                InstanceId = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlicerServices", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SlicerSettings",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Enabled = table.Column<bool>(type: "bit", nullable: false),
                PerEngineJson = table.Column<string>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                JitterPercent = table.Column<double>(type: "float", nullable: false, defaultValue: 15.0)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlicerSettings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Workers",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ServiceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                EndpointUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                TotalSlots = table.Column<int>(type: "int", nullable: false),
                ActiveJobs = table.Column<int>(type: "int", nullable: false),
                CompletedJobs = table.Column<int>(type: "int", nullable: false),
                FailedJobs = table.Column<int>(type: "int", nullable: false),
                AverageProcessingTimeSeconds = table.Column<double>(type: "float", nullable: true),
                LastHeartbeat = table.Column<DateTime>(type: "datetime2", nullable: true),
                RegisteredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                OnlineAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                OfflineAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ApiKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IsDisabled = table.Column<bool>(type: "bit", nullable: false),
                DisabledReason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                ArtifactsProduced = table.Column<int>(type: "int", nullable: false),
                ArtifactBytesProduced = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Workers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MachineProfiles",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                Manufacturer = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                SlicerType = table.Column<int>(type: "int", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MachineModelProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                IsDefault = table.Column<bool>(type: "bit", nullable: false),
                IsPublic = table.Column<bool>(type: "bit", nullable: false),
                SlicerVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MachineProfiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_MachineProfiles_MachineModelProfiles_MachineModelProfileId",
                    column: x => x.MachineModelProfileId,
                    principalSchema: "slicer",
                    principalTable: "MachineModelProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "SliceJobs",
            schema: "slicer",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ModelFileUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                ModelFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                SlicerEngine = table.Column<int>(type: "int", nullable: false),
                SlicerProfileJson = table.Column<string>(type: "TEXT", nullable: true),
                SlicerProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RequiredCapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Priority = table.Column<int>(type: "int", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Checksum = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ResultFileUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                ProgressPercent = table.Column<int>(type: "int", nullable: false),
                ProgressMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                EstimatedPrintTimeSeconds = table.Column<int>(type: "int", nullable: true),
                FilamentUsedGrams = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ArtifactIdsCsv = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ArtifactsCount = table.Column<int>(type: "int", nullable: true),
                ArtifactsTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                RetryCount = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SliceJobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_SliceJobs_ProcessProfiles_SlicerProfileId",
                    column: x => x.SlicerProfileId,
                    principalSchema: "slicer",
                    principalTable: "ProcessProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_CreatedAt",
            schema: "slicer",
            table: "Artifacts",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_JobId",
            schema: "slicer",
            table: "Artifacts",
            column: "JobId");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_JobId_Kind",
            schema: "slicer",
            table: "Artifacts",
            columns: new[] { "JobId", "Kind" });

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_WorkerId",
            schema: "slicer",
            table: "Artifacts",
            column: "WorkerId");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentProfiles_CreatedByUserId",
            schema: "slicer",
            table: "FilamentProfiles",
            column: "CreatedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentProfiles_Hash",
            schema: "slicer",
            table: "FilamentProfiles",
            column: "Hash",
            unique: true,
            filter: "[Hash] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentProfiles_IsSystem",
            schema: "slicer",
            table: "FilamentProfiles",
            column: "IsSystem");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentProfiles_Material",
            schema: "slicer",
            table: "FilamentProfiles",
            column: "Material");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentProfiles_Name_Material_SlicerType",
            schema: "slicer",
            table: "FilamentProfiles",
            columns: new[] { "Name", "Material", "SlicerType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FilamentProfiles_SlicerType",
            schema: "slicer",
            table: "FilamentProfiles",
            column: "SlicerType");

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_Hash",
            schema: "slicer",
            table: "MachineModelProfiles",
            column: "Hash",
            unique: true,
            filter: "[Hash] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_IsSystem",
            schema: "slicer",
            table: "MachineModelProfiles",
            column: "IsSystem");

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_Manufacturer",
            schema: "slicer",
            table: "MachineModelProfiles",
            column: "Manufacturer");

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_Name_SlicerType",
            schema: "slicer",
            table: "MachineModelProfiles",
            columns: new[] { "Name", "SlicerType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_PrinterModelId",
            schema: "slicer",
            table: "MachineModelProfiles",
            column: "PrinterModelId");

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_CreatedByUserId",
            schema: "slicer",
            table: "MachineProfiles",
            column: "CreatedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_Hash",
            schema: "slicer",
            table: "MachineProfiles",
            column: "Hash",
            unique: true,
            filter: "[Hash] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_IsSystem",
            schema: "slicer",
            table: "MachineProfiles",
            column: "IsSystem");

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_MachineModelProfileId",
            schema: "slicer",
            table: "MachineProfiles",
            column: "MachineModelProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_Manufacturer",
            schema: "slicer",
            table: "MachineProfiles",
            column: "Manufacturer");

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_Name_SlicerType",
            schema: "slicer",
            table: "MachineProfiles",
            columns: new[] { "Name", "SlicerType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_PrinterModelId",
            schema: "slicer",
            table: "MachineProfiles",
            column: "PrinterModelId");

        migrationBuilder.CreateIndex(
            name: "IX_MachineProfiles_SlicerType",
            schema: "slicer",
            table: "MachineProfiles",
            column: "SlicerType");

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_FileFormat",
            schema: "slicer",
            table: "Models3D",
            column: "FileFormat");

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_FileHash",
            schema: "slicer",
            table: "Models3D",
            column: "FileHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_FolderId",
            schema: "slicer",
            table: "Models3D",
            column: "FolderId");

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_HealthStatus",
            schema: "slicer",
            table: "Models3D",
            column: "HealthStatus");

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_IsValid",
            schema: "slicer",
            table: "Models3D",
            column: "IsValid");

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_LastHealthCheckDate",
            schema: "slicer",
            table: "Models3D",
            column: "LastHealthCheckDate");

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_UploadedAt",
            schema: "slicer",
            table: "Models3D",
            column: "UploadedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_UploadedByUserId",
            schema: "slicer",
            table: "Models3D",
            column: "UploadedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_CreatedByUserId",
            schema: "slicer",
            table: "ProcessProfiles",
            column: "CreatedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_Hash",
            schema: "slicer",
            table: "ProcessProfiles",
            column: "Hash",
            unique: true,
            filter: "[Hash] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_IsDefault",
            schema: "slicer",
            table: "ProcessProfiles",
            column: "IsDefault");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_IsPublic",
            schema: "slicer",
            table: "ProcessProfiles",
            column: "IsPublic");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_IsSystem",
            schema: "slicer",
            table: "ProcessProfiles",
            column: "IsSystem");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_Name_SlicerType_PrinterModelId",
            schema: "slicer",
            table: "ProcessProfiles",
            columns: new[] { "Name", "SlicerType", "PrinterModelId" },
            unique: true,
            filter: "[PrinterModelId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_PrinterModelId",
            schema: "slicer",
            table: "ProcessProfiles",
            column: "PrinterModelId");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_SlicerType",
            schema: "slicer",
            table: "ProcessProfiles",
            column: "SlicerType");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_PrinterId",
            schema: "slicer",
            table: "SliceJobs",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_QueuedAt",
            schema: "slicer",
            table: "SliceJobs",
            column: "QueuedAt");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_SlicerProfileId",
            schema: "slicer",
            table: "SliceJobs",
            column: "SlicerProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Status",
            schema: "slicer",
            table: "SliceJobs",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Status_Priority_QueuedAt",
            schema: "slicer",
            table: "SliceJobs",
            columns: new[] { "Status", "Priority", "QueuedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_UserId",
            schema: "slicer",
            table: "SliceJobs",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_WorkerId",
            schema: "slicer",
            table: "SliceJobs",
            column: "WorkerId");

        migrationBuilder.CreateIndex(
            name: "IX_SlicerServices_Name",
            schema: "slicer",
            table: "SlicerServices",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_SlicerServices_SlicerType",
            schema: "slicer",
            table: "SlicerServices",
            column: "SlicerType");

        migrationBuilder.CreateIndex(
            name: "IX_SlicerServices_Status",
            schema: "slicer",
            table: "SlicerServices",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_Workers_LastHeartbeat",
            schema: "slicer",
            table: "Workers",
            column: "LastHeartbeat");

        migrationBuilder.CreateIndex(
            name: "IX_Workers_ServiceId",
            schema: "slicer",
            table: "Workers",
            column: "ServiceId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Workers_Status",
            schema: "slicer",
            table: "Workers",
            column: "Status");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Artifacts",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "FilamentProfiles",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "MachineProfiles",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "Models3D",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "SliceJobs",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "SlicerServices",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "SlicerSettings",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "Workers",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "MachineModelProfiles",
            schema: "slicer");

        migrationBuilder.DropTable(
            name: "ProcessProfiles",
            schema: "slicer");
    }
}
