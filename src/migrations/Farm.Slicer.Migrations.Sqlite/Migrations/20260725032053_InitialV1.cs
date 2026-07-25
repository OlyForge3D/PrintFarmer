using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class InitialV1 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Artifacts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkerId = table.Column<Guid>(type: "TEXT", nullable: true),
                Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Artifacts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FilamentProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Material = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Manufacturer = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                SlicerType = table.Column<int>(type: "INTEGER", nullable: false),
                NozzleTemperature = table.Column<int>(type: "INTEGER", nullable: false),
                BedTemperature = table.Column<int>(type: "INTEGER", nullable: false),
                PrintSpeed = table.Column<int>(type: "INTEGER", nullable: false),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CompatiblePrinters = table.Column<string>(type: "TEXT", nullable: true),
                IsSystem = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                SlicerVersion = table.Column<string>(type: "TEXT", nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MachineModelProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Manufacturer = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                SlicerType = table.Column<int>(type: "INTEGER", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                IsSystem = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                SlicerVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MachineModelProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Models3D",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                FileFormat = table.Column<int>(type: "INTEGER", nullable: false),
                DimensionX = table.Column<double>(type: "REAL", nullable: true),
                DimensionY = table.Column<double>(type: "REAL", nullable: true),
                DimensionZ = table.Column<double>(type: "REAL", nullable: true),
                TriangleCount = table.Column<int>(type: "INTEGER", nullable: true),
                IsValid = table.Column<bool>(type: "INTEGER", nullable: false),
                ValidationErrors = table.Column<string>(type: "TEXT", nullable: true),
                UploadedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                FilePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                ThumbnailFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                FileHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastHealthCheckDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                HealthStatus = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                LastVerificationResult = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Models3D", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProcessProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                SlicerType = table.Column<int>(type: "INTEGER", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                SpecificPrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                LayerHeight = table.Column<double>(type: "REAL", nullable: false),
                InfillPercentage = table.Column<int>(type: "INTEGER", nullable: false),
                PrintSpeed = table.Column<double>(type: "REAL", nullable: false),
                EnableSupports = table.Column<bool>(type: "INTEGER", nullable: false),
                Quality = table.Column<int>(type: "INTEGER", nullable: false),
                AdvancedSettings = table.Column<string>(type: "TEXT", nullable: true),
                SlicerVersion = table.Column<string>(type: "TEXT", nullable: true),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CompatiblePrinters = table.Column<string>(type: "TEXT", nullable: true),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                IsSystem = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProcessProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SlicerServices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                SlicerType = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Host = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                UiManifestUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                MaxConcurrentJobs = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                ApiKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                ApiKeyRotatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Tags = table.Column<string>(type: "TEXT", nullable: true),
                InstanceId = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlicerServices", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SlicerSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                PerEngineJson = table.Column<string>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                JitterPercent = table.Column<double>(type: "REAL", nullable: false, defaultValue: 15.0)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlicerSettings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Workers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ServiceId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EndpointUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                TotalSlots = table.Column<int>(type: "INTEGER", nullable: false),
                ActiveJobs = table.Column<int>(type: "INTEGER", nullable: false),
                CompletedJobs = table.Column<int>(type: "INTEGER", nullable: false),
                FailedJobs = table.Column<int>(type: "INTEGER", nullable: false),
                AverageProcessingTimeSeconds = table.Column<double>(type: "REAL", nullable: true),
                LastHeartbeat = table.Column<DateTime>(type: "TEXT", nullable: true),
                RegisteredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                OnlineAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                OfflineAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ApiKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                DisabledReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                ArtifactsProduced = table.Column<int>(type: "INTEGER", nullable: false),
                ArtifactBytesProduced = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Workers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MachineProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Manufacturer = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                SlicerType = table.Column<int>(type: "INTEGER", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                MachineModelProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                RawJson = table.Column<string>(type: "TEXT", nullable: true),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                IsSystem = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                SlicerVersion = table.Column<string>(type: "TEXT", nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MachineProfiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_MachineProfiles_MachineModelProfiles_MachineModelProfileId",
                    column: x => x.MachineModelProfileId,
                    principalTable: "MachineModelProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "SliceJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                ModelFileUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                ModelFileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                SlicerEngine = table.Column<int>(type: "INTEGER", nullable: false),
                SlicerProfileJson = table.Column<string>(type: "TEXT", nullable: true),
                SlicerProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                RequiredCapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                Priority = table.Column<int>(type: "INTEGER", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CorrelationId = table.Column<Guid>(type: "TEXT", nullable: true),
                Checksum = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ResultFileUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                ProgressMessage = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                EstimatedPrintTimeSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                FilamentUsedGrams = table.Column<decimal>(type: "TEXT", nullable: true),
                WorkerId = table.Column<Guid>(type: "TEXT", nullable: true),
                ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LeaseExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ArtifactIdsCsv = table.Column<string>(type: "TEXT", nullable: true),
                ArtifactsCount = table.Column<int>(type: "INTEGER", nullable: true),
                ArtifactsTotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                RetryCount = table.Column<int>(type: "INTEGER", nullable: false)
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
            unique: true);

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
            name: "IX_MachineModelProfiles_Hash",
            table: "MachineModelProfiles",
            column: "Hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_IsSystem",
            table: "MachineModelProfiles",
            column: "IsSystem");

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_Manufacturer",
            table: "MachineModelProfiles",
            column: "Manufacturer");

        migrationBuilder.CreateIndex(
            name: "IX_MachineModelProfiles_Name_SlicerType",
            table: "MachineModelProfiles",
            columns: new[] { "Name", "SlicerType" },
            unique: true);

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
            unique: true);

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
            unique: true);

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
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_PrinterModelId",
            table: "ProcessProfiles",
            column: "PrinterModelId");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessProfiles_SlicerType",
            table: "ProcessProfiles",
            column: "SlicerType");

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
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
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
    }
}
