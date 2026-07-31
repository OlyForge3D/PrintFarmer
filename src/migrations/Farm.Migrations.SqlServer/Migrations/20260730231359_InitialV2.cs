using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class InitialV2 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ApiKeys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                Purpose = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                Scopes = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ApiKeys", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AppSettingsEntities",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AppSettingsEntities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AttentionSnoozes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttentionItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                SnoozedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                AttentionItemAnchorAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AttentionSnoozes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BarcodeScanLogs",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                Barcode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                HttpStatus = table.Column<int>(type: "int", nullable: false),
                MatchedFilamentId = table.Column<int>(type: "int", nullable: true),
                CreatedSpoolId = table.Column<int>(type: "int", nullable: true),
                BinId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PartInventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                Message = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BarcodeScanLogs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BedClearCommandRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                RequestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                JobRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                DispatchStateRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                QueueRevision = table.Column<long>(type: "bigint", nullable: false),
                PrinterConfigRevision = table.Column<long>(type: "bigint", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                OutboxEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DispatchAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BedClearCommandRecords", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BedTypes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                IsSystem = table.Column<bool>(type: "bit", nullable: false),
                Color = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BedTypes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Bins",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Bins", x => x.Id);
                table.CheckConstraint("CK_Bins_Code_Normalized", "\"Code\" = UPPER(\"Code\")");
            });

        migrationBuilder.CreateTable(
            name: "CalibrationBlobCleanups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OpaqueStorageKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationBlobCleanups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationChangeFeedStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                LastSequence = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationChangeFeedStates", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationSyncCursors",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Scope = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationSyncCursors", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogVersions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Version = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ManifestHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Source = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogVersions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CustomFieldDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EntityType = table.Column<int>(type: "int", nullable: false),
                FieldName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                FieldKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                FieldType = table.Column<int>(type: "int", nullable: false),
                Options = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                IsRequired = table.Column<bool>(type: "bit", nullable: false),
                SortOrder = table.Column<int>(type: "int", nullable: false),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                DefaultValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomFieldDefinitions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "DispatchSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                AutoDispatchEnabled = table.Column<bool>(type: "bit", nullable: false),
                AutoDispatchMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                IdleThresholdSeconds = table.Column<int>(type: "int", nullable: false),
                MinimumScoreThreshold = table.Column<double>(type: "float", nullable: false),
                MaxConcurrentDispatches = table.Column<int>(type: "int", nullable: false),
                LoadBalancingStrategy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DispatchSettings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FailedLoginAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Identifier = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                AttemptedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                FailureReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FailedLoginAttempts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FilamentSwapOverrides",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ToolheadIndex = table.Column<int>(type: "int", nullable: false),
                SpoolId = table.Column<int>(type: "int", nullable: false),
                UserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                ExpectedMaterial = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ScannedMaterial = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                AffectedJobIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentSwapOverrides", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FilamentTypes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                DefaultHotendTemp = table.Column<double>(type: "float", nullable: true),
                DefaultBedTemp = table.Column<double>(type: "float", nullable: true),
                IsAbrasive = table.Column<bool>(type: "bit", nullable: false),
                NeedsEnclosure = table.Column<bool>(type: "bit", nullable: false),
                DefaultPricePerKg = table.Column<double>(type: "float", nullable: true),
                DefaultDensity = table.Column<double>(type: "float", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentTypes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FileHealthAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AuditDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                AuditType = table.Column<int>(type: "int", nullable: false),
                FilesChecked = table.Column<int>(type: "int", nullable: false),
                HealthyFiles = table.Column<int>(type: "int", nullable: false),
                MissingFiles = table.Column<int>(type: "int", nullable: false),
                CorruptedFiles = table.Column<int>(type: "int", nullable: false),
                OrphanedFiles = table.Column<int>(type: "int", nullable: false),
                MissingFileIds = table.Column<string>(type: "TEXT", nullable: true),
                CorruptedFileIds = table.Column<string>(type: "TEXT", nullable: true),
                OrphanedFilePaths = table.Column<string>(type: "TEXT", nullable: true),
                SummaryMessage = table.Column<string>(type: "TEXT", nullable: true),
                HasIssues = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileHealthAudits", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FolderNode",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Path = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                FolderType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FolderNode", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "GcodePromotionCheckpoints",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OperationScope = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                OperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                RequestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SourceSliceJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SourceWorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceContentSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SourceSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                CalibrationProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationOrchestrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                State = table.Column<int>(type: "int", nullable: false),
                FailureCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ReconcileAttempts = table.Column<int>(type: "int", nullable: false),
                SourceAcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GcodePromotionCheckpoints", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "IdempotencyRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                RouteKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                ResponseStatusCode = table.Column<int>(type: "int", nullable: true),
                ResponseContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                ResponseBody = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "LibrarySyncChanges",
            columns: table => new
            {
                Revision = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                EntityType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Visibility = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibrarySyncChanges", x => x.Revision);
            });

        migrationBuilder.CreateTable(
            name: "Locations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                PrinterCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Path = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false, defaultValue: "/"),
                Depth = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                SortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                TotalPrinterCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Locations", x => x.Id);
                table.ForeignKey(
                    name: "FK_Locations_Locations_ParentId",
                    column: x => x.ParentId,
                    principalTable: "Locations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LoginAuditEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                Success = table.Column<bool>(type: "bit", nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                FailureReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LoginAuditEntries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceComponents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Sku = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                UnitCost = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                Supplier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                InStock = table.Column<int>(type: "int", nullable: false),
                MinimumStock = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceComponents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                IntervalHours = table.Column<double>(type: "float", nullable: true),
                IntervalDays = table.Column<int>(type: "int", nullable: true),
                EstimatedDurationMinutes = table.Column<int>(type: "int", nullable: true),
                Priority = table.Column<int>(type: "int", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                IsDefault = table.Column<bool>(type: "bit", nullable: false),
                RequiresEnclosure = table.Column<bool>(type: "bit", nullable: true),
                RequiresCarbonFilter = table.Column<bool>(type: "bit", nullable: true),
                RequiresHepaFilter = table.Column<bool>(type: "bit", nullable: true),
                RequiresBowdenTube = table.Column<bool>(type: "bit", nullable: true),
                RequiresPtfeLiner = table.Column<bool>(type: "bit", nullable: true),
                RequiresLinearRails = table.Column<bool>(type: "bit", nullable: true),
                RequiresLeadScrews = table.Column<bool>(type: "bit", nullable: true),
                RequiresToolchanger = table.Column<bool>(type: "bit", nullable: true),
                RequiresFilamentCutter = table.Column<bool>(type: "bit", nullable: true),
                RequiresHeatedChamber = table.Column<bool>(type: "bit", nullable: true),
                RequiresHeatedBed = table.Column<bool>(type: "bit", nullable: true),
                RequiresMultiMaterial = table.Column<bool>(type: "bit", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceTasks", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Manufacturers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Url = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                NameLowered = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Manufacturers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaterialClusters",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaterialClusters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ModelCollections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IsShared = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ModelCollections", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MutationCounters",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Value = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MutationCounters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ObicoServers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                ApiKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                MaxConcurrentAnalyses = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ObicoServers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OutboxSequenceStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                NextSequence = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxSequenceStates", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PasswordPolicies",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                MinLength = table.Column<int>(type: "int", nullable: false),
                RequireUppercase = table.Column<bool>(type: "bit", nullable: false),
                RequireLowercase = table.Column<bool>(type: "bit", nullable: false),
                RequireDigit = table.Column<bool>(type: "bit", nullable: false),
                RequireSymbol = table.Column<bool>(type: "bit", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PasswordPolicies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrinterGroups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterGroups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                Priority = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                DueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjectTemplates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                DefaultPriority = table.Column<int>(type: "int", nullable: false),
                DefaultNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                IsSystemTemplate = table.Column<bool>(type: "bit", nullable: false),
                SortOrder = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjectTemplates", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "QueueDispatchOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                AggregateType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AggregateRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                JobRevision = table.Column<long>(type: "bigint", nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                JobStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                JobKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                PrinterConfigRevision = table.Column<long>(type: "bigint", nullable: true),
                DispatchStateRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                DispatchStateRevision = table.Column<long>(type: "bigint", nullable: true),
                AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                AttemptNumber = table.Column<int>(type: "int", nullable: true),
                AttemptOutcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                BedClearState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                BedClearCommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                BedClearExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                FailureCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                FailureRetryable = table.Column<bool>(type: "bit", nullable: true),
                FailureRequiresReconciliation = table.Column<bool>(type: "bit", nullable: true),
                EventType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                SchemaVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                LastAttemptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                RetryAfterUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueueDispatchOutbox", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "QueueOperationAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                ResourceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DispatchAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Operation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                JobRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                DispatchStateRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                IdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                DetailJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueueOperationAudits", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "QueuePositionStates",
            columns: table => new
            {
                ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                NextPosition = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueuePositionStates", x => x.ScopeId);
            });

        migrationBuilder.CreateTable(
            name: "Resources",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                ResourceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Resources", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RetryPolicies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                MaxRetries = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                InitialDelaySeconds = table.Column<int>(type: "int", nullable: false, defaultValue: 60),
                ExponentialBase = table.Column<double>(type: "float", nullable: false, defaultValue: 2.0),
                MaxDelaySeconds = table.Column<int>(type: "int", nullable: false, defaultValue: 3600),
                RetryOnErrorCategories = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "Recoverable"),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RetryPolicies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Roles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                IsSystemRole = table.Column<bool>(type: "bit", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Roles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SpoolmanConfigs",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BaseUrl = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SpoolmanConfigs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SystemLogs",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                Level = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Message = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                Exception = table.Column<string>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                Metadata = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SystemLogs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Tags",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "manual"),
                IsAutoGenerated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                Color = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tags", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UserActions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserActions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Username = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                PasswordHash = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                EmailConfirmationToken = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                PasswordResetToken = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                PasswordResetExpires = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastLogin = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                FailedLoginAttempts = table.Column<int>(type: "int", nullable: false),
                LockoutEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastFailedLogin = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WebhookSubscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                Secret = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                EventTypes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                ConsecutiveFailures = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                MaxConsecutiveFailures = table.Column<int>(type: "int", nullable: false, defaultValue: 10),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastDeliveryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastSuccessAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PartInventories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                ModelFileRef = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                DefaultBinId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OnHand = table.Column<int>(type: "int", nullable: false),
                ReorderPoint = table.Column<int>(type: "int", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PartInventories", x => x.Id);
                table.CheckConstraint("CK_PartInventories_OnHand_NonNegative", "\"OnHand\" >= 0");
                table.CheckConstraint("CK_PartInventories_ReorderPoint_NonNegative", "\"ReorderPoint\" >= 0");
                table.CheckConstraint("CK_PartInventories_Sku_Normalized", "\"Sku\" = UPPER(\"Sku\")");
                table.ForeignKey(
                    name: "FK_PartInventories_Bins_DefaultBinId",
                    column: x => x.DefaultBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "CustomFieldValues",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomFieldValues", x => x.Id);
                table.ForeignKey(
                    name: "FK_CustomFieldValues_CustomFieldDefinitions_DefinitionId",
                    column: x => x.DefinitionId,
                    principalTable: "CustomFieldDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceTaskComponents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MaintenanceTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MaintenanceComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Quantity = table.Column<int>(type: "int", nullable: false),
                Notes = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceTaskComponents", x => x.Id);
                table.ForeignKey(
                    name: "FK_MaintenanceTaskComponents_MaintenanceComponents_MaintenanceComponentId",
                    column: x => x.MaintenanceComponentId,
                    principalTable: "MaintenanceComponents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MaintenanceTaskComponents_MaintenanceTasks_MaintenanceTaskId",
                    column: x => x.MaintenanceTaskId,
                    principalTable: "MaintenanceTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ExtruderModelDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                GearRatio = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                IsDirectDrive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExtruderModelDefinitions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ExtruderModelDefinitions_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "HotendModelDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MaxTemp = table.Column<int>(type: "int", nullable: true, defaultValue: 300),
                IsHighFlow = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                MaxFlowRate = table.Column<double>(type: "float", nullable: true),
                NozzleInterface = table.Column<int>(type: "int", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HotendModelDefinitions", x => x.Id);
                table.ForeignKey(
                    name: "FK_HotendModelDefinitions_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "NozzleModelDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Diameter = table.Column<double>(type: "float", nullable: false),
                MaxTemp = table.Column<int>(type: "int", nullable: true, defaultValue: 500),
                NozzleType = table.Column<int>(type: "int", nullable: false),
                NozzleInterface = table.Column<int>(type: "int", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NozzleModelDefinitions", x => x.Id);
                table.ForeignKey(
                    name: "FK_NozzleModelDefinitions_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "PrinterModels",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MotionType = table.Column<int>(type: "int", nullable: true),
                MaxX = table.Column<double>(type: "float", nullable: true),
                MaxY = table.Column<double>(type: "float", nullable: true),
                MaxZ = table.Column<double>(type: "float", nullable: true),
                DefaultBackend = table.Column<int>(type: "int", nullable: true),
                HasHeatedBed = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                HasEnclosure = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasCarbonFilter = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasHepaFilter = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasBowdenTube = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasPtfeLiner = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasLinearRails = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasLeadScrews = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasToolchanger = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasFilamentCutter = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                HasHeatedChamber = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                MultiMaterial = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                SupportsAutoLeveling = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                MaxBedTemp = table.Column<int>(type: "int", nullable: true, defaultValue: 120),
                MaxPrintSpeed = table.Column<int>(type: "int", nullable: true, defaultValue: 150),
                CoverImageUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                BedTextureUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                DefaultWattage = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                DefaultHourlyRate = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                DefaultAutoDispatchState = table.Column<int>(type: "int", nullable: false),
                DefaultStartBehavior = table.Column<int>(type: "int", nullable: true),
                DefaultBedTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                NameLowered = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterModels", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterModels_BedTypes_DefaultBedTypeId",
                    column: x => x.DefaultBedTypeId,
                    principalTable: "BedTypes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PrinterModels_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "MaterialClusterMembers",
            columns: table => new
            {
                ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FilamentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaterialClusterMembers", x => new { x.ClusterId, x.FilamentTypeId });
                table.ForeignKey(
                    name: "FK_MaterialClusterMembers_FilamentTypes_FilamentTypeId",
                    column: x => x.FilamentTypeId,
                    principalTable: "FilamentTypes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MaterialClusterMembers_MaterialClusters_ClusterId",
                    column: x => x.ClusterId,
                    principalTable: "MaterialClusters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ModelCollectionMemberships",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ModelCollectionMemberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_ModelCollectionMemberships_ModelCollections_CollectionId",
                    column: x => x.CollectionId,
                    principalTable: "ModelCollections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjectTemplateFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintProjectTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                FileNamePattern = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                ColorRequirement = table.Column<int>(type: "int", nullable: false),
                MaterialRequirement = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                PrintCount = table.Column<int>(type: "int", nullable: false),
                SortOrder = table.Column<int>(type: "int", nullable: false),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjectTemplateFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintProjectTemplateFiles_PrintProjectTemplates_PrintProjectTemplateId",
                    column: x => x.PrintProjectTemplateId,
                    principalTable: "PrintProjectTemplates",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterGroupAccesses",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccessLevel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterGroupAccesses", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterGroupAccesses_PrinterGroups_PrinterGroupId",
                    column: x => x.PrinterGroupId,
                    principalTable: "PrinterGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterGroupAccesses_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Model3DTag",
            columns: table => new
            {
                Model3DId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TagsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Model3DTag", x => new { x.Model3DId, x.TagsId });
                table.ForeignKey(
                    name: "FK_Model3DTag_Tags_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RolePermissions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Granted = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RolePermissions", x => x.Id);
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
                table.ForeignKey(
                    name: "FK_RolePermissions_UserActions_ActionId",
                    column: x => x.ActionId,
                    principalTable: "UserActions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AuthAuditLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                EventType = table.Column<int>(type: "int", nullable: false),
                Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Success = table.Column<bool>(type: "bit", nullable: false),
                FailureReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Metadata = table.Column<string>(type: "TEXT", nullable: true),
                CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthAuditLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_AuthAuditLogs_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DeviceTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RegistrationVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                InstallationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                Token = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                AppBundleId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastFailureAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeviceTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_DeviceTokens_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "NotificationPreferences",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EnableEmailNotifications = table.Column<bool>(type: "bit", nullable: false),
                EnablePushNotifications = table.Column<bool>(type: "bit", nullable: false),
                EnableInAppNotifications = table.Column<bool>(type: "bit", nullable: false),
                EnableTelegramNotifications = table.Column<bool>(type: "bit", nullable: false),
                NotifyOnCompletion = table.Column<bool>(type: "bit", nullable: false),
                NotifyOnFailure = table.Column<bool>(type: "bit", nullable: false),
                NotifyOnStart = table.Column<bool>(type: "bit", nullable: false),
                NotifyOnPause = table.Column<bool>(type: "bit", nullable: false),
                InAppOnJobStarted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                InAppOnJobCompleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                InAppOnJobFailed = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                InAppOnJobPaused = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                EmailOnJobStarted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                EmailOnJobCompleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                EmailOnJobFailed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                EmailOnJobPaused = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PushOnJobStarted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PushOnJobCompleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                PushOnJobFailed = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                PushOnJobPaused = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                TelegramOnJobStarted = table.Column<bool>(type: "bit", nullable: false),
                TelegramOnJobCompleted = table.Column<bool>(type: "bit", nullable: false),
                TelegramOnJobFailed = table.Column<bool>(type: "bit", nullable: false),
                TelegramOnJobPaused = table.Column<bool>(type: "bit", nullable: false),
                InAppOnPrinterFailure = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                EmailOnPrinterFailure = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PushOnPrinterFailure = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                TelegramOnPrinterFailure = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                InAppOnFilamentRunout = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                EmailOnFilamentRunout = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PushOnFilamentRunout = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                TelegramOnFilamentRunout = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                InAppOnHarvestReady = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                EmailOnHarvestReady = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PushOnHarvestReady = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                TelegramOnHarvestReady = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                InAppOnMaintenanceDue = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                EmailOnMaintenanceDue = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PushOnMaintenanceDue = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                TelegramOnMaintenanceDue = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                InAppOnPrinterOffline = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                EmailOnPrinterOffline = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PushOnPrinterOffline = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                TelegramOnPrinterOffline = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                AttentionPushCategoryPreferencesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Frequency = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                RetentionDays = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                table.ForeignKey(
                    name: "FK_NotificationPreferences_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PasswordResetTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Token = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IsUsed = table.Column<bool>(type: "bit", nullable: false),
                UsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                UsedByIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PasswordResetTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_PasswordResetTokens_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintQuotas",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                GroupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                QuotaType = table.Column<int>(type: "int", nullable: false),
                LimitAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                UsedAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                PeriodType = table.Column<int>(type: "int", nullable: false),
                PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                ResetAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintQuotas", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintQuotas_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PushSubscriptions",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Endpoint = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                P256dh = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                Auth = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_PushSubscriptions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RefreshTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Token = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IsRevoked = table.Column<bool>(type: "bit", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                RevokedByIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                ReplacedByToken = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                CreatedByIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_RefreshTokens_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RevokedTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                RevokedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RevokedTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_RevokedTokens_Users_RevokedByUserId",
                    column: x => x.RevokedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_RevokedTokens_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "UserBalances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BalanceAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false, defaultValue: "USD"),
                LastUpdated = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserBalances", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserBalances_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserPasskeyCredentials",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CredentialId = table.Column<byte[]>(type: "varbinary(900)", nullable: false),
                PublicKey = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                SignCount = table.Column<long>(type: "bigint", nullable: false),
                DeviceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                AaguidDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserPasskeyCredentials", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserPasskeyCredentials_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserQuotaGroupMemberships",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                GroupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserQuotaGroupMemberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserQuotaGroupMemberships_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserRoles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false)
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
            name: "UserSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Theme = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Locale = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                ItemsPerPage = table.Column<int>(type: "int", nullable: false),
                DefaultSlicerPreset = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                PrintablesUsername = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PrintablesOAuthAccessToken = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                PrintablesOAuthRefreshToken = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                PrintablesOAuthTokenType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                PrintablesOAuthScope = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                PrintablesOAuthTokenExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                PrintablesOAuthLinkedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserSettings", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserSettings_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskType = table.Column<int>(type: "int", nullable: false),
                EntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                Status = table.Column<int>(type: "int", nullable: false),
                Priority = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                DueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                DismissedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                DismissedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RelatedEntityIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastMutationSequence = table.Column<long>(type: "bigint", nullable: false),
                AnchorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                AnchorAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                WindowStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                WindowEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                SourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                SourceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserTasks", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserTasks_Users_DismissedByUserId",
                    column: x => x.DismissedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "WebhookDeliveryLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WebhookSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                StatusCode = table.Column<int>(type: "int", nullable: true),
                Success = table.Column<bool>(type: "bit", nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                Attempt = table.Column<int>(type: "int", nullable: false),
                DurationMs = table.Column<long>(type: "bigint", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookDeliveryLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_WebhookDeliveryLogs_WebhookSubscriptions_WebhookSubscriptionId",
                    column: x => x.WebhookSubscriptionId,
                    principalTable: "WebhookSubscriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ToolheadModelDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DefaultHotendId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DefaultExtruderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DefaultNozzleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ToolheadModelDefinitions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ToolheadModelDefinitions_ExtruderModelDefinitions_DefaultExtruderId",
                    column: x => x.DefaultExtruderId,
                    principalTable: "ExtruderModelDefinitions",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_ToolheadModelDefinitions_HotendModelDefinitions_DefaultHotendId",
                    column: x => x.DefaultHotendId,
                    principalTable: "HotendModelDefinitions",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ToolheadModelDefinitions_NozzleModelDefinitions_DefaultNozzleId",
                    column: x => x.DefaultNozzleId,
                    principalTable: "NozzleModelDefinitions",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "FilamentTypePrinterModel",
            columns: table => new
            {
                PrinterModelsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SupportedFilamentTypesId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentTypePrinterModel", x => new { x.PrinterModelsId, x.SupportedFilamentTypesId });
                table.ForeignKey(
                    name: "FK_FilamentTypePrinterModel_FilamentTypes_SupportedFilamentTypesId",
                    column: x => x.SupportedFilamentTypesId,
                    principalTable: "FilamentTypes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_FilamentTypePrinterModel_PrinterModels_PrinterModelsId",
                    column: x => x.PrinterModelsId,
                    principalTable: "PrinterModels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterModelAliases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SlicerModelName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                SlicerType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterModelAliases", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterModelAliases_PrinterModels_PrinterModelId",
                    column: x => x.PrinterModelId,
                    principalTable: "PrinterModels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Printers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ServerUrl = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                OriginalServerUrl = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                BackendPort = table.Column<int>(type: "int", nullable: false),
                FrontendPort = table.Column<int>(type: "int", nullable: true),
                Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Backend = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                ConfigurationRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                CalibrationConfigurationUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                FirmwareFamily = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                GcodeDialect = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                FirmwareDetectionSource = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                FirmwareVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                FirmwareDetectionVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                FirmwareDetectionConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                FirmwareDetectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                FirmwareIdentityVerified = table.Column<bool>(type: "bit", nullable: false),
                BackendVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                BackendApiVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Username = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Password = table.Column<string>(type: "nvarchar(max)", nullable: true),
                BuddyCameraIp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TemplateMachineProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrinterGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                BedTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DateAcquired = table.Column<DateTime>(type: "datetime2", nullable: true),
                MaxBuildVolumeX = table.Column<double>(type: "float", nullable: true),
                MaxBuildVolumeY = table.Column<double>(type: "float", nullable: true),
                MaxBuildVolumeZ = table.Column<double>(type: "float", nullable: true),
                HasHeatedBed = table.Column<bool>(type: "bit", nullable: false),
                HasEnclosure = table.Column<bool>(type: "bit", nullable: false),
                NozzleDiameter = table.Column<double>(type: "float", nullable: true),
                HasMmu = table.Column<bool>(type: "bit", nullable: true),
                MultiMaterial = table.Column<bool>(type: "bit", nullable: false),
                SupportsAutoLeveling = table.Column<bool>(type: "bit", nullable: false),
                SupportsPerToolAttribution = table.Column<bool>(type: "bit", nullable: false),
                MaxPrintSpeed = table.Column<int>(type: "int", nullable: true),
                BedOriginX = table.Column<double>(type: "float", nullable: true),
                BedOriginY = table.Column<double>(type: "float", nullable: true),
                PrintablePolygonJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ExcludedRegionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CalibrationMotionType = table.Column<int>(type: "int", nullable: true),
                MaxTravelSpeed = table.Column<int>(type: "int", nullable: true),
                MaxAcceleration = table.Column<int>(type: "int", nullable: true),
                MaxTravelAcceleration = table.Column<int>(type: "int", nullable: true),
                CalibrationHasHeatedBed = table.Column<bool>(type: "bit", nullable: true),
                CalibrationHasEnclosure = table.Column<bool>(type: "bit", nullable: true),
                HasHeatedChamber = table.Column<bool>(type: "bit", nullable: true),
                MaxChamberTemp = table.Column<int>(type: "int", nullable: true),
                ActiveToolheadIndex = table.Column<int>(type: "int", nullable: true),
                SupportsPressureAdvance = table.Column<bool>(type: "bit", nullable: true),
                SupportsFirmwareRetraction = table.Column<bool>(type: "bit", nullable: true),
                CalibrationHardwareVerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CalibrationSlicerEngine = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CalibrationSlicerDistribution = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CalibrationSlicerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CalibrationProfileFormat = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CalibrationMachineProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationProcessProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationFilamentProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MaxBedTemp = table.Column<int>(type: "int", nullable: true),
                CurrentMaterial = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CurrentSpoolId = table.Column<int>(type: "int", nullable: true),
                IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                Wattage = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                MachineHourlyRate = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                ObicoEnabled = table.Column<bool>(type: "bit", nullable: false),
                ZOffsetMm = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                LastZOffsetCalibrationAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                InMaintenance = table.Column<bool>(type: "bit", nullable: false),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                AutoDispatchEnabled = table.Column<bool>(type: "bit", nullable: false),
                UseModelDispatchDefaults = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Printers", x => x.Id);
                table.ForeignKey(
                    name: "FK_Printers_BedTypes_BedTypeId",
                    column: x => x.BedTypeId,
                    principalTable: "BedTypes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Printers_Locations_LocationId",
                    column: x => x.LocationId,
                    principalTable: "Locations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Printers_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Printers_PrinterGroups_PrinterGroupId",
                    column: x => x.PrinterGroupId,
                    principalTable: "PrinterGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Printers_PrinterModels_ModelId",
                    column: x => x.ModelId,
                    principalTable: "PrinterModels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "BalanceTransactions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserBalanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                TransactionType = table.Column<int>(type: "int", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                PerformedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BalanceTransactions", x => x.Id);
                table.ForeignKey(
                    name: "FK_BalanceTransactions_UserBalances_UserBalanceId",
                    column: x => x.UserBalanceId,
                    principalTable: "UserBalances",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterModelToolheads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Index = table.Column<int>(type: "int", nullable: false),
                HotendModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExtruderModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ToolheadModelDefId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                NozzleModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SupportedMaterials = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IsPrimary = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterModelToolheads", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterModelToolheads_ExtruderModelDefinitions_ExtruderModelId",
                    column: x => x.ExtruderModelId,
                    principalTable: "ExtruderModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PrinterModelToolheads_HotendModelDefinitions_HotendModelId",
                    column: x => x.HotendModelId,
                    principalTable: "HotendModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PrinterModelToolheads_NozzleModelDefinitions_NozzleModelId",
                    column: x => x.NozzleModelId,
                    principalTable: "NozzleModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PrinterModelToolheads_PrinterModels_PrinterModelId",
                    column: x => x.PrinterModelId,
                    principalTable: "PrinterModels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterModelToolheads_ToolheadModelDefinitions_ToolheadModelDefId",
                    column: x => x.ToolheadModelDefId,
                    principalTable: "ToolheadModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "Cameras",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                StreamUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                SnapshotUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                SortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                Location = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                Source = table.Column<string>(type: "nvarchar(450)", nullable: false),
                CameraType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                HealthStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                LastHealthCheck = table.Column<DateTime>(type: "datetime2", nullable: true),
                HealthMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                ConsecutiveFailures = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Cameras", x => x.Id);
                table.ForeignKey(
                    name: "FK_Cameras_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FailureDetectionIncidents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                JobName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                Confidence = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                SnapshotUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                AutoPaused = table.Column<bool>(type: "bit", nullable: false),
                ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FailureDetectionIncidents", x => x.Id);
                table.ForeignKey(
                    name: "FK_FailureDetectionIncidents_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FilamentFallbackGroups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                NameNormalized = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                MaterialType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                DisplayOrder = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentFallbackGroups", x => x.Id);
                table.ForeignKey(
                    name: "FK_FilamentFallbackGroups_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "GcodeFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Source = table.Column<int>(type: "int", nullable: false),
                SourcePrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OriginalPrinterPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                LastSeenOnPrinter = table.Column<DateTime>(type: "datetime2", nullable: true),
                RequiredNozzleDiameter = table.Column<double>(type: "float", nullable: true),
                RequiredMaterial = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                EstimatedPrintTimeMinutes = table.Column<double>(type: "float", nullable: true),
                EstimatedFilamentLengthMm = table.Column<double>(type: "float", nullable: true),
                EstimatedFilamentWeightG = table.Column<double>(type: "float", nullable: true),
                ExtractedPrinterModelName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SlicerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                SlicerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PrintSettingsId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                LayerHeight = table.Column<double>(type: "float", nullable: true),
                InfillPercentage = table.Column<double>(type: "float", nullable: true),
                Perimeters = table.Column<int>(type: "int", nullable: true),
                PrintTemperature = table.Column<double>(type: "float", nullable: true),
                BedTemperature = table.Column<double>(type: "float", nullable: true),
                PrintSpeed = table.Column<double>(type: "float", nullable: true),
                TotalLayers = table.Column<int>(type: "int", nullable: true),
                FirstLayerHeight = table.Column<double>(type: "float", nullable: true),
                SupportEnabled = table.Column<bool>(type: "bit", nullable: true),
                ToolChangesCount = table.Column<int>(type: "int", nullable: true),
                ObjectDimensionX = table.Column<double>(type: "float", nullable: true),
                ObjectDimensionY = table.Column<double>(type: "float", nullable: true),
                ObjectDimensionZ = table.Column<double>(type: "float", nullable: true),
                ObjectCount = table.Column<int>(type: "int", nullable: true),
                RetractionLength = table.Column<double>(type: "float", nullable: true),
                RetractionSpeed = table.Column<double>(type: "float", nullable: true),
                TopSolidLayers = table.Column<int>(type: "int", nullable: true),
                BottomSolidLayers = table.Column<int>(type: "int", nullable: true),
                MaxVolumetricSpeed = table.Column<double>(type: "float", nullable: true),
                IroningEnabled = table.Column<bool>(type: "bit", nullable: true),
                PrinterGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                FilamentPerExtruderWeightG = table.Column<string>(type: "nvarchar(max)", nullable: true),
                FilamentPerExtruderLengthMm = table.Column<string>(type: "nvarchar(max)", nullable: true),
                FilamentPerExtruderColorHex = table.Column<string>(type: "nvarchar(max)", nullable: true),
                FilamentPerExtruderType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ExtruderCount = table.Column<int>(type: "int", nullable: true),
                SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceSliceJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceWorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationOrchestrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PromotionOperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                PromotionOperationKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PromotionCorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ContentSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SpecificationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SourceModelSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                MachineProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ProcessProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                FilamentProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SlicerEngineName = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                SlicerDistribution = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PinnedSlicerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SlicerContainerDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                FirmwareFamily = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                GcodeDialect = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                GeneratorName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                GeneratorVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CalibrationManifestJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CalibrationManifestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                IsImmutable = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                PromotedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
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
                table.PrimaryKey("PK_GcodeFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_GcodeFiles_FolderNode_FolderId",
                    column: x => x.FolderId,
                    principalTable: "FolderNode",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_GcodeFiles_PrinterGroups_PrinterGroupId",
                    column: x => x.PrinterGroupId,
                    principalTable: "PrinterGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_GcodeFiles_PrinterModels_PrinterModelId",
                    column: x => x.PrinterModelId,
                    principalTable: "PrinterModels",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_GcodeFiles_Printers_SourcePrinterId",
                    column: x => x.SourcePrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "GcodeHarvestOperations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                Status = table.Column<int>(type: "int", nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ErrorType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ErrorPhase = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ErrorDetails = table.Column<string>(type: "nvarchar(max)", nullable: true),
                FailedResource = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IsRetryable = table.Column<bool>(type: "bit", nullable: false),
                ErrorOccurredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                FilesFound = table.Column<int>(type: "int", nullable: false),
                FilesAdded = table.Column<int>(type: "int", nullable: false),
                FilesSkipped = table.Column<int>(type: "int", nullable: false),
                FilesErrored = table.Column<int>(type: "int", nullable: false),
                TotalBytesProcessed = table.Column<long>(type: "bigint", nullable: false),
                IncludeSubdirectories = table.Column<bool>(type: "bit", nullable: false),
                MaxFileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                ModifiedAfter = table.Column<DateTime>(type: "datetime2", nullable: true),
                FileExtensions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                MinFileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                DuplicateHandling = table.Column<string>(type: "nvarchar(max)", nullable: true)
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
            name: "GcodeHarvestQueueItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ProcessingStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                Priority = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                Status = table.Column<int>(type: "int", nullable: false),
                Parameters = table.Column<string>(type: "TEXT", nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ErrorDetails = table.Column<string>(type: "TEXT", nullable: true),
                FilesFound = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                FilesAdded = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                FilesSkipped = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                FilesErrored = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GcodeHarvestQueueItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_GcodeHarvestQueueItems_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MaintenancePlans",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ManufacturerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MotionType = table.Column<int>(type: "int", nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                IsDefault = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenancePlans", x => x.Id);
                table.ForeignKey(
                    name: "FK_MaintenancePlans_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_MaintenancePlans_PrinterModels_PrinterModelId",
                    column: x => x.PrinterModelId,
                    principalTable: "PrinterModels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_MaintenancePlans_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "NfcDevices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                FirmwareVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                WifiRssi = table.Column<int>(type: "int", nullable: true),
                NfcReaderOk = table.Column<bool>(type: "bit", nullable: false),
                FreeHeap = table.Column<int>(type: "int", nullable: true),
                LastHeartbeat = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastScanAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastScannedSpoolId = table.Column<int>(type: "int", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NfcDevices", x => x.Id);
                table.ForeignKey(
                    name: "FK_NfcDevices_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "NfcTagBindings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TagUid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SpoolId = table.Column<int>(type: "int", nullable: true),
                SpoolName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                TrayId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SpoolLastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NfcTagBindings", x => x.Id);
                table.ForeignKey(
                    name: "FK_NfcTagBindings_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "PowerMonitors",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProviderType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                DeviceAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                ElectricityRateUsdPerKwh = table.Column<decimal>(type: "decimal(10,4)", precision: 10, scale: 4, nullable: false),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PowerMonitors", x => x.Id);
                table.ForeignKey(
                    name: "FK_PowerMonitors_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterDispatchStates",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AutoDispatchState = table.Column<int>(type: "int", nullable: false),
                BedPreConfirmed = table.Column<bool>(type: "bit", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                QueueRevision = table.Column<long>(type: "bigint", nullable: false),
                AcknowledgedJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                AcknowledgedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                AcknowledgementIdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                AcknowledgementExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                AcknowledgedJobRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                AcknowledgedQueueRevision = table.Column<long>(type: "bigint", nullable: true),
                AcknowledgedPrinterConfigRevision = table.Column<long>(type: "bigint", nullable: true),
                ActiveJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ActiveDispatchAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PhysicalControlCommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PhysicalControlAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PhysicalControlOperation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PhysicalControlActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                PhysicalControlStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                PhysicalControlRequiresReconciliation = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterDispatchStates", x => x.PrinterId);
                table.ForeignKey(
                    name: "FK_PrinterDispatchStates_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterServiceState",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LastHistorySeedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastModelSyncAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastCapabilityUpdate = table.Column<DateTime>(type: "datetime2", nullable: false),
                ObicoServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterServiceState", x => x.PrinterId);
                table.ForeignKey(
                    name: "FK_PrinterServiceState_ObicoServers_ObicoServerId",
                    column: x => x.ObicoServerId,
                    principalTable: "ObicoServers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PrinterServiceState_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterStatisticsSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TotalPrintHours = table.Column<double>(type: "float", nullable: false),
                ExternalPrintHours = table.Column<double>(type: "float", nullable: false),
                ExternalJobsCompleted = table.Column<long>(type: "bigint", nullable: false),
                ExternalBaselineInitializedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastExternalHoursAttributionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                TotalJobsCompleted = table.Column<int>(type: "int", nullable: false),
                TotalJobsFailed = table.Column<int>(type: "int", nullable: false),
                TotalFilamentUsedGrams = table.Column<double>(type: "float", nullable: false),
                TotalFilamentUsedMeters = table.Column<double>(type: "float", nullable: false),
                LastSyncTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterStatisticsSet", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterStatisticsSet_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterTag",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TagsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterTag", x => new { x.PrinterId, x.TagsId });
                table.ForeignKey(
                    name: "FK_PrinterTag_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterTag_Tags_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Spools",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Material = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Sku = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                LotNumber = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                WeightGrams = table.Column<double>(type: "float", nullable: false),
                ColorHex = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                InUse = table.Column<bool>(type: "bit", nullable: false),
                AssignedPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
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
            name: "Toolheads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Index = table.Column<int>(type: "int", nullable: false),
                HotendModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExtruderModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ToolheadModelDefId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                NozzleModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SupportedMaterials = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IsPrimary = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                ToolheadType = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                OffsetX = table.Column<double>(type: "float", nullable: true),
                OffsetY = table.Column<double>(type: "float", nullable: true),
                OffsetZ = table.Column<double>(type: "float", nullable: true),
                NozzleDiameter = table.Column<double>(type: "float", nullable: true),
                NozzleType = table.Column<int>(type: "int", nullable: true),
                NozzleMaterial = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                NozzleMaxTemperature = table.Column<int>(type: "int", nullable: true),
                NozzleIsHardened = table.Column<bool>(type: "bit", nullable: true),
                HotendMaxTemperature = table.Column<int>(type: "int", nullable: true),
                MaxVolumetricFlow = table.Column<double>(type: "float", nullable: true),
                DriveType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                IsDirectDrive = table.Column<bool>(type: "bit", nullable: true),
                ExtruderGearRatio = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CurrentSpoolId = table.Column<int>(type: "int", nullable: true),
                CurrentMaterial = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CumulativePrintHours = table.Column<double>(type: "float", nullable: false),
                CurrentFilamentColor = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Toolheads", x => x.Id);
                table.ForeignKey(
                    name: "FK_Toolheads_ExtruderModelDefinitions_ExtruderModelId",
                    column: x => x.ExtruderModelId,
                    principalTable: "ExtruderModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Toolheads_HotendModelDefinitions_HotendModelId",
                    column: x => x.HotendModelId,
                    principalTable: "HotendModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Toolheads_NozzleModelDefinitions_NozzleModelId",
                    column: x => x.NozzleModelId,
                    principalTable: "NozzleModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Toolheads_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_Toolheads_ToolheadModelDefinitions_ToolheadModelDefId",
                    column: x => x.ToolheadModelDefId,
                    principalTable: "ToolheadModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "GcodeFileTag",
            columns: table => new
            {
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TagsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GcodeFileTag", x => new { x.GcodeFileId, x.TagsId });
                table.ForeignKey(
                    name: "FK_GcodeFileTag_GcodeFiles_GcodeFileId",
                    column: x => x.GcodeFileId,
                    principalTable: "GcodeFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_GcodeFileTag_Tags_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                AssignedPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Status = table.Column<int>(type: "int", nullable: false),
                Priority = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                QueuePosition = table.Column<int>(type: "int", nullable: false),
                RequiredNozzleDiameter = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                RequiredMaterialType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RequiredMaterialsPerToolJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RequiredCapabilities = table.Column<string>(type: "nvarchar(max)", nullable: true),
                EstimatedPrintTime = table.Column<long>(type: "bigint", nullable: true),
                EstimatedFilamentUsage = table.Column<double>(type: "float", nullable: true),
                ActualStartTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                ActualEndTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                ActualPrintTime = table.Column<long>(type: "bigint", nullable: true),
                ActualFilamentUsage = table.Column<double>(type: "float", nullable: true),
                EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                ActualCost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                MaterialCostUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                KwhUsed = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                EnergyCostUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                MachineTimeCostUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                LaborCostUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                TotalCostUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                CostCalculatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PreferredPrinterIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ExcludedPrinterIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                DeadlineAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                ExternalJobId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                SourcePrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                WasSeededFromHistory = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                IsExternalPrint = table.Column<bool>(type: "bit", nullable: false),
                Copies = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                CompletedCopies = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                ProjectFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ProjectName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                SpoolmanFilamentId = table.Column<int>(type: "int", nullable: true),
                SpoolmanSpoolId = table.Column<int>(type: "int", nullable: true),
                FilamentName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                FilamentVendor = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                FilamentColor = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                PlateIndex = table.Column<int>(type: "int", nullable: true),
                PlateName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                DispatchedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                DispatchScore = table.Column<double>(type: "float", nullable: true),
                DispatchMode = table.Column<int>(type: "int", nullable: true),
                HarvestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                HarvestOperationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                HarvestedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                HarvestedIntoBinId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                JobKind = table.Column<int>(type: "int", nullable: true),
                CalibrationProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationConfigSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationOrchestrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SliceJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                GcodeContentSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PinnedGcodeFileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                CreatorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                IdempotencyScope = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                IdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                IdempotencyRequestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                RequiredFirmwareFamily = table.Column<int>(type: "int", nullable: true),
                RequiredGcodeDialect = table.Column<int>(type: "int", nullable: true),
                RequiredSlicerEngine = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                RequiredSlicerDistribution = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                RequiredSlicerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                RequiredSlicerContainerDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                SpecificationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                MachineProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ProcessProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                FilamentProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PrinterConfigSnapshotSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PinnedPrinterConfigRevision = table.Column<long>(type: "bigint", nullable: true),
                PinnedPrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PinnedToolheadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PinnedToolheadIndex = table.Column<int>(type: "int", nullable: true),
                PinnedSpoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PinnedFilamentSku = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                PinnedFilamentLotNumber = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ActiveExternalPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                FilamentSnapshotSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SourceModelSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CalibrationManifestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PinnedObjectDimensionX = table.Column<double>(type: "float", nullable: true),
                PinnedObjectDimensionY = table.Column<double>(type: "float", nullable: true),
                PinnedObjectDimensionZ = table.Column<double>(type: "float", nullable: true),
                BlockedReasonCode = table.Column<int>(type: "int", nullable: true),
                BlockedReasonJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintJobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintJobs_Bins_HarvestedIntoBinId",
                    column: x => x.HarvestedIntoBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
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
            name: "HarvestDiscoveredFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                HarvestOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FilePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                FileName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Size = table.Column<long>(type: "bigint", nullable: false),
                ThumbnailUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Status = table.Column<int>(type: "int", nullable: false),
                Error = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                DiscoveredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                AlreadyInLibrary = table.Column<bool>(type: "bit", nullable: false),
                FileHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ExtractedNozzleDiameter = table.Column<double>(type: "float", nullable: true),
                ExtractedMaterial = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ExtractedPrintTime = table.Column<double>(type: "float", nullable: true),
                ExtractedFilamentLength = table.Column<double>(type: "float", nullable: true),
                ExtractedSlicerName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ExtractedSlicerVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HarvestDiscoveredFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_HarvestDiscoveredFiles_GcodeHarvestOperations_HarvestOperationId",
                    column: x => x.HarvestOperationId,
                    principalTable: "GcodeHarvestOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PlanTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MaintenancePlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MaintenanceTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SortOrder = table.Column<int>(type: "int", nullable: false),
                IntervalHoursOverride = table.Column<double>(type: "float", nullable: true),
                IntervalDaysOverride = table.Column<int>(type: "int", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlanTasks", x => x.Id);
                table.ForeignKey(
                    name: "FK_PlanTasks_MaintenancePlans_MaintenancePlanId",
                    column: x => x.MaintenancePlanId,
                    principalTable: "MaintenancePlans",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PlanTasks_MaintenanceTasks_MaintenanceTaskId",
                    column: x => x.MaintenanceTaskId,
                    principalTable: "MaintenanceTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "NfcScanEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                NfcDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SpoolId = table.Column<int>(type: "int", nullable: true),
                TagFormat = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                MaterialType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                BrandName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NfcScanEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_NfcScanEvents_NfcDevices_NfcDeviceId",
                    column: x => x.NfcDeviceId,
                    principalTable: "NfcDevices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PowerReadings",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                PowerMonitorId = table.Column<int>(type: "int", nullable: false),
                WattsNow = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                KwhTotal = table.Column<decimal>(type: "decimal(14,4)", precision: 14, scale: 4, nullable: true),
                RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PowerReadings", x => x.Id);
                table.ForeignKey(
                    name: "FK_PowerReadings_PowerMonitors_PowerMonitorId",
                    column: x => x.PowerMonitorId,
                    principalTable: "PowerMonitors",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationProjects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                LifecycleStatus = table.Column<int>(type: "int", nullable: false),
                ExperienceMode = table.Column<int>(type: "int", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CurrentPrinterConfigurationSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SelectedToolheadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SelectedToolheadIndex = table.Column<int>(type: "int", nullable: true),
                FilamentProvider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                FilamentProductId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                FilamentSku = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                FilamentVendor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                FilamentProductName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                FilamentMaterial = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                FilamentDiameter = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: true),
                FilamentColor = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                FilamentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SpoolmanFilamentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LocalSpoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SpoolmanSpoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                FilamentSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                OrderedStepsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CurrentStep = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                CurrentSelectionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                CreateRequestId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                UpdatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                DeletedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationProjects", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationProjects_FilamentTypes_FilamentTypeId",
                    column: x => x.FilamentTypeId,
                    principalTable: "FilamentTypes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_CalibrationProjects_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_CalibrationProjects_Spools_LocalSpoolId",
                    column: x => x.LocalSpoolId,
                    principalTable: "Spools",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "FilamentFallbackGroupMembers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FallbackGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ToolheadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Position = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentFallbackGroupMembers", x => x.Id);
                table.ForeignKey(
                    name: "FK_FilamentFallbackGroupMembers_FilamentFallbackGroups_FallbackGroupId",
                    column: x => x.FallbackGroupId,
                    principalTable: "FilamentFallbackGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_FilamentFallbackGroupMembers_Toolheads_ToolheadId",
                    column: x => x.ToolheadId,
                    principalTable: "Toolheads",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "PrinterMaintenanceSchedules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MaintenancePlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                ToolheadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DeployedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterMaintenanceSchedules", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterMaintenanceSchedules_MaintenancePlans_MaintenancePlanId",
                    column: x => x.MaintenancePlanId,
                    principalTable: "MaintenancePlans",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrinterMaintenanceSchedules_Toolheads_ToolheadId",
                    column: x => x.ToolheadId,
                    principalTable: "Toolheads",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "CameraSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                EventType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                FilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                FileSizeBytes = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CameraSnapshots", x => x.Id);
                table.ForeignKey(
                    name: "FK_CameraSnapshots_Cameras_CameraId",
                    column: x => x.CameraId,
                    principalTable: "Cameras",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_CameraSnapshots_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_CameraSnapshots_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DispatchLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                DispatchMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                Score = table.Column<double>(type: "float", nullable: true),
                ScoreBreakdown = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                ScoringDetails = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                DispatchedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DispatchLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_DispatchLogs_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_DispatchLogs_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "JobRetries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OriginalJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RetryJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttemptNumber = table.Column<int>(type: "int", nullable: false),
                ErrorCategory = table.Column<int>(type: "int", nullable: false),
                FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                ScheduledRetryTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                ActualRetryTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Pending"),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobRetries", x => x.Id);
                table.ForeignKey(
                    name: "FK_JobRetries_PrintJobs_OriginalJobId",
                    column: x => x.OriginalJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_JobRetries_PrintJobs_RetryJobId",
                    column: x => x.RetryJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "JobSchedules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RootPrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ScheduledStartTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                TimeZone = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "UTC"),
                RecurrencePattern = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RecurrenceInterval = table.Column<int>(type: "int", nullable: false),
                RecurrenceEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                IsPaused = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                ScheduledAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                InitiatingActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                RequiresOperatorReauthorization = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobSchedules", x => x.Id);
                table.ForeignKey(
                    name: "FK_JobSchedules_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "JobStateHistories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FromState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                ToState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                TransitionedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                DurationInState = table.Column<long>(type: "bigint", nullable: true),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobStateHistories", x => x.Id);
                table.ForeignKey(
                    name: "FK_JobStateHistories_PrintJobs_JobId",
                    column: x => x.JobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Notifications",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Type = table.Column<int>(type: "int", nullable: false),
                Subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                Body = table.Column<string>(type: "TEXT", nullable: false),
                Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IsRead = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Notifications", x => x.Id);
                table.ForeignKey(
                    name: "FK_Notifications_PrintJobs_JobId",
                    column: x => x.JobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Notifications_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PartInventoryAdjustments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BinId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Delta = table.Column<int>(type: "int", nullable: false),
                ResultingBalance = table.Column<int>(type: "int", nullable: false),
                Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OperationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PartInventoryAdjustments", x => x.Id);
                table.CheckConstraint("CK_PartInventoryAdjustments_Delta_NonZero", "\"Delta\" <> 0");
                table.CheckConstraint("CK_PartInventoryAdjustments_ResultingBalance_NonNegative", "\"ResultingBalance\" >= 0");
                table.ForeignKey(
                    name: "FK_PartInventoryAdjustments_Bins_BinId",
                    column: x => x.BinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartInventoryAdjustments_PartInventories_PartInventoryId",
                    column: x => x.PartInventoryId,
                    principalTable: "PartInventories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartInventoryAdjustments_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "PrintApprovals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintApprovals", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintApprovals_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintJobPartOutputSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                QuantityPerPrint = table.Column<int>(type: "int", nullable: false),
                ExpectedBinId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExpectedBinCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                SourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                SourceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SourceMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintJobPartOutputSnapshots", x => x.Id);
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_ExpectedBin_Consistent", "(\"ExpectedBinId\" IS NULL AND \"ExpectedBinCode\" IS NULL) OR (\"ExpectedBinId\" IS NOT NULL AND \"ExpectedBinCode\" IS NOT NULL)");
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_Quantity_Positive", "\"QuantityPerPrint\" > 0");
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_Sequence_NonNegative", "\"Sequence\" >= 0");
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_Sku_Normalized", "\"Sku\" = UPPER(\"Sku\")");
                table.ForeignKey(
                    name: "FK_PrintJobPartOutputSnapshots_Bins_ExpectedBinId",
                    column: x => x.ExpectedBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrintJobPartOutputSnapshots_PartInventories_PartInventoryId",
                    column: x => x.PartInventoryId,
                    principalTable: "PartInventories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrintJobPartOutputSnapshots_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintJobStatistics",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActualDurationMs = table.Column<long>(type: "bigint", nullable: true),
                EstimatedDurationMs = table.Column<long>(type: "bigint", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Material = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                NozzleTemperature = table.Column<int>(type: "int", nullable: true),
                BedTemperature = table.Column<int>(type: "int", nullable: true),
                SpeedPercentage = table.Column<int>(type: "int", nullable: false),
                IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                ActualCost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintJobStatistics", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintJobStatistics_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrintJobStatistics_PrinterModels_PrinterModelId",
                    column: x => x.PrinterModelId,
                    principalTable: "PrinterModels",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "PrintJobTag",
            columns: table => new
            {
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TagsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintJobTag", x => new { x.PrintJobId, x.TagsId });
                table.ForeignKey(
                    name: "FK_PrintJobTag_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrintJobTag_Tags_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintJobToolheadUsages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ToolheadIndex = table.Column<int>(type: "int", nullable: false),
                SpoolmanSpoolId = table.Column<int>(type: "int", nullable: true),
                SpoolSourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                SpoolSourceIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                FilamentUsageGrams = table.Column<double>(type: "float", nullable: true),
                IsFilamentUsageAuthoritative = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                SlicerEstimateGrams = table.Column<double>(type: "float", nullable: true),
                FilamentName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                FilamentColor = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                MaterialCostUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintJobToolheadUsages", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintJobToolheadUsages_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjectFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                PrintProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SpoolmanFilamentId = table.Column<int>(type: "int", nullable: true),
                MaterialRequirement = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PrintCount = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                PrintedCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                SortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                PlateIndex = table.Column<int>(type: "int", nullable: true),
                PlateName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastPrintedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastPrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjectFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintProjectFiles_GcodeFiles_GcodeFileId",
                    column: x => x.GcodeFileId,
                    principalTable: "GcodeFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrintProjectFiles_PrintJobs_LastPrintJobId",
                    column: x => x.LastPrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PrintProjectFiles_PrintProjects_PrintProjectId",
                    column: x => x.PrintProjectId,
                    principalTable: "PrintProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "QueueDispatchAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterConfigRevision = table.Column<long>(type: "bigint", nullable: false),
                AttemptNumber = table.Column<int>(type: "int", nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                StartPathKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                AcknowledgementIdempotencyKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ClaimedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                BackendAcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                Outcome = table.Column<int>(type: "int", nullable: false),
                ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ErrorDetail = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                IsRetryable = table.Column<bool>(type: "bit", nullable: false),
                RequiresReconciliation = table.Column<bool>(type: "bit", nullable: false),
                BackendJobId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                BackendFileIdentity = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                BackendCommandId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                BackendFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                BackendCallPhase = table.Column<int>(type: "int", nullable: false),
                BackendCorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                BackendCallStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                BackendResponseAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                ReconciliationCount = table.Column<int>(type: "int", nullable: false),
                LastReconciledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                TerminalAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                JobRowVersionAtClaim = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                DispatchStateRowVersionAtClaim = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueueDispatchAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_QueueDispatchAttempts_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "HarvestFileGcodeFileMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                HarvestDiscoveredFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HarvestFileGcodeFileMappings", x => x.Id);
                table.ForeignKey(
                    name: "FK_HarvestFileGcodeFileMappings_GcodeFiles_GcodeFileId",
                    column: x => x.GcodeFileId,
                    principalTable: "GcodeFiles",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_HarvestFileGcodeFileMappings_HarvestDiscoveredFiles_HarvestDiscoveredFileId",
                    column: x => x.HarvestDiscoveredFileId,
                    principalTable: "HarvestDiscoveredFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationChanges",
            columns: table => new
            {
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EntityType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EntityRevision = table.Column<long>(type: "bigint", nullable: false),
                ChangeType = table.Column<int>(type: "int", nullable: false),
                TombstoneJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                MutationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationChanges", x => x.Sequence);
                table.ForeignKey(
                    name: "FK_CalibrationChanges_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationDrafts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StepId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DeviceLineageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Method = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                PrerequisitesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                UpdatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationDrafts", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationDrafts_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationIdempotencyRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Scope = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                OperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                OperationType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CanonicalRequestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ResourceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                StoredStatusCode = table.Column<int>(type: "int", nullable: false),
                StoredResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                State = table.Column<int>(type: "int", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationIdempotencyRecords", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationIdempotencyRecords_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrinterConfigurationSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SchemaVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                SanitizedSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SnapshotSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                PrinterConfigurationRevision = table.Column<long>(type: "bigint", nullable: false),
                FirmwareFamily = table.Column<int>(type: "int", nullable: false),
                GcodeDialect = table.Column<int>(type: "int", nullable: false),
                FirmwareDetectionSource = table.Column<int>(type: "int", nullable: false),
                FirmwareVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Backend = table.Column<int>(type: "int", nullable: false),
                BackendVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                BackendApiVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                SlicerEngine = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SlicerDistribution = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SlicerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SlicerContainerDigest = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                MachineProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExactMachineProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                MachineProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ProcessProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExactProcessProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ProcessProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                FilamentProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExactFilamentProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                FilamentProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CapturedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
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

        migrationBuilder.CreateTable(
            name: "MaintenanceAlerts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterMaintenanceScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MaintenanceTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ToolheadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Title = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Message = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                Severity = table.Column<int>(type: "int", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                PrinterHoursAtTrigger = table.Column<double>(type: "float", nullable: false),
                HoursSinceLastMaintenance = table.Column<double>(type: "float", nullable: true),
                DaysSinceLastMaintenance = table.Column<int>(type: "int", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                AcknowledgedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ResolvedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                DismissedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                DismissedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                DismissalReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceAlerts", x => x.Id);
                table.ForeignKey(
                    name: "FK_MaintenanceAlerts_MaintenanceTasks_MaintenanceTaskId",
                    column: x => x.MaintenanceTaskId,
                    principalTable: "MaintenanceTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_MaintenanceAlerts_PrinterMaintenanceSchedules_PrinterMaintenanceScheduleId",
                    column: x => x.PrinterMaintenanceScheduleId,
                    principalTable: "PrinterMaintenanceSchedules",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_MaintenanceAlerts_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MaintenanceAlerts_Toolheads_ToolheadId",
                    column: x => x.ToolheadId,
                    principalTable: "Toolheads",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "JobExecutions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                JobScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OccurrencePrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DispatchAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ScheduledExecutionTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                ActualStartTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobExecutions", x => x.Id);
                table.ForeignKey(
                    name: "FK_JobExecutions_JobSchedules_JobScheduleId",
                    column: x => x.JobScheduleId,
                    principalTable: "JobSchedules",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PartHarvestOutputSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartInventoryAdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JobOutputSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Quantity = table.Column<int>(type: "int", nullable: false),
                ExpectedBinId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExpectedBinCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ActualBinId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActualBinCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Origin = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                SourceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OverrideApplied = table.Column<bool>(type: "bit", nullable: false),
                OverrideReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                Sequence = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PartHarvestOutputSnapshots", x => x.Id);
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_ExpectedBin_Consistent", "(\"ExpectedBinId\" IS NULL AND \"ExpectedBinCode\" IS NULL) OR (\"ExpectedBinId\" IS NOT NULL AND \"ExpectedBinCode\" IS NOT NULL)");
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_Quantity_Positive", "\"Quantity\" > 0");
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_Sequence_NonNegative", "\"Sequence\" >= 0");
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_Sku_Normalized", "\"Sku\" = UPPER(\"Sku\")");
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_Bins_ActualBinId",
                    column: x => x.ActualBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_Bins_ExpectedBinId",
                    column: x => x.ExpectedBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_PartInventories_PartInventoryId",
                    column: x => x.PartInventoryId,
                    principalTable: "PartInventories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_PartInventoryAdjustments_PartInventoryAdjustmentId",
                    column: x => x.PartInventoryAdjustmentId,
                    principalTable: "PartInventoryAdjustments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PartOutputMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrintProjectFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Quantity = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PartOutputMappings", x => x.Id);
                table.CheckConstraint("CK_PartOutputMappings_ExactlyOneSource", "(\"GcodeFileId\" IS NULL AND \"PrintProjectFileId\" IS NOT NULL) OR (\"GcodeFileId\" IS NOT NULL AND \"PrintProjectFileId\" IS NULL)");
                table.CheckConstraint("CK_PartOutputMappings_Quantity_Positive", "\"Quantity\" > 0");
                table.ForeignKey(
                    name: "FK_PartOutputMappings_GcodeFiles_GcodeFileId",
                    column: x => x.GcodeFileId,
                    principalTable: "GcodeFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartOutputMappings_PartInventories_PartInventoryId",
                    column: x => x.PartInventoryId,
                    principalTable: "PartInventories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PartOutputMappings_PrintProjectFiles_PrintProjectFileId",
                    column: x => x.PrintProjectFileId,
                    principalTable: "PrintProjectFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                ParentAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationKind = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Method = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DefinitionVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                InputJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SpecificationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SpecificationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                PrinterConfigurationSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProfileSnapshotIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ActualSpoolSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                AttemptRequestId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationAttempts_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CalibrationAttempts_PrinterConfigurationSnapshots_PrinterConfigurationSnapshotId",
                    column: x => x.PrinterConfigurationSnapshotId,
                    principalTable: "PrinterConfigurationSnapshots",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterMaintenanceScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ResolvedAlertId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MaintenanceTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ToolheadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                TaskName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                Component = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PerformedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                PerformedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                DurationMinutes = table.Column<int>(type: "int", nullable: true),
                PartsReplaced = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Cost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                PrinterHoursAtMaintenance = table.Column<double>(type: "float", nullable: true),
                ToolheadHoursAtMaintenance = table.Column<double>(type: "float", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
                    column: x => x.ResolvedAlertId,
                    principalTable: "MaintenanceAlerts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MaintenanceLogs_MaintenanceTasks_MaintenanceTaskId",
                    column: x => x.MaintenanceTaskId,
                    principalTable: "MaintenanceTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_MaintenanceLogs_PrinterMaintenanceSchedules_PrinterMaintenanceScheduleId",
                    column: x => x.PrinterMaintenanceScheduleId,
                    principalTable: "PrinterMaintenanceSchedules",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_MaintenanceLogs_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MaintenanceLogs_Toolheads_ToolheadId",
                    column: x => x.ToolheadId,
                    principalTable: "Toolheads",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationAttemptEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                EventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DerivedStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Model3DId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SliceJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CalibrationOrchestrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ErrorJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RetryNumber = table.Column<int>(type: "int", nullable: true),
                OperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationAttemptEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationAttemptEvents_CalibrationAttempts_AttemptId",
                    column: x => x.AttemptId,
                    principalTable: "CalibrationAttempts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CalibrationAttemptEvents_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationObservations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                ObservationType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                MeasurementsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                UnitsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                RetestRecommended = table.Column<bool>(type: "bit", nullable: false),
                Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                SelectionParentObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SelectionReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                OperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationObservations", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationObservations_CalibrationAttempts_AttemptId",
                    column: x => x.AttemptId,
                    principalTable: "CalibrationAttempts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CalibrationObservations_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationOrchestrations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CurrentStep = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                RetryCount = table.Column<int>(type: "int", nullable: false),
                NextRetryAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                LastErrorJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Model3DId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SliceJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                GenerationRequestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SpecificationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                PlanManifestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                GcodeSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ManifestSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                GeneratorVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SlicerContainerDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                SlicerBinarySha256 = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                FinalArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PromotionOperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                StepStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationOrchestrations", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationOrchestrations_CalibrationAttempts_AttemptId",
                    column: x => x.AttemptId,
                    principalTable: "CalibrationAttempts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CalibrationOrchestrations_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "CalibrationPhotos",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClientUploadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                OpaqueStorageKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Width = table.Column<int>(type: "int", nullable: false),
                Height = table.Column<int>(type: "int", nullable: false),
                CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                Caption = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                SortOrder = table.Column<int>(type: "int", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                DeletedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                DeleteRequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                PurgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationPhotos", x => x.Id);
                table.ForeignKey(
                    name: "FK_CalibrationPhotos_CalibrationAttempts_AttemptId",
                    column: x => x.AttemptId,
                    principalTable: "CalibrationAttempts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CalibrationPhotos_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "GeneratedProfileRevisions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SourceAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ParentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RevisionNumber = table.Column<long>(type: "bigint", nullable: false),
                ProfileType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SchemaVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                SlicerEngine = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SlicerDistribution = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SlicerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SlicerContainerDigest = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                NormalizedSettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                FlowRatio = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                PressureAdvance = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                PressureAdvanceSmoothTime = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                RetractionLength = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                RetractionSpeed = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                RetractionMinimumTravel = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                RetractionLiftZ = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                NozzleTemperature = table.Column<int>(type: "int", nullable: true),
                BedTemperature = table.Column<int>(type: "int", nullable: true),
                MaximumVolumetricFlow = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                SourceMachineProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceProcessProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceFilamentProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceProfileFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ExactProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                GeneratorVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                GenerationRequestId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GeneratedProfileRevisions", x => x.Id);
                table.ForeignKey(
                    name: "FK_GeneratedProfileRevisions_CalibrationAttempts_SourceAttemptId",
                    column: x => x.SourceAttemptId,
                    principalTable: "CalibrationAttempts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_GeneratedProfileRevisions_CalibrationProjects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "CalibrationProjects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "GeneratedProfileRevisionOperations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                GeneratedProfileRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OperationType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                OperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                PublishedProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExportFormat = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GeneratedProfileRevisionOperations", x => x.Id);
                table.ForeignKey(
                    name: "FK_GeneratedProfileRevisionOperations_GeneratedProfileRevisions_GeneratedProfileRevisionId",
                    column: x => x.GeneratedProfileRevisionId,
                    principalTable: "GeneratedProfileRevisions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.InsertData(
            table: "CalibrationChangeFeedStates",
            columns: new[] { "Id", "LastSequence" },
            values: new object[] { 1, 0L });

        migrationBuilder.InsertData(
            table: "DispatchSettings",
            columns: new[] { "Id", "AutoDispatchEnabled", "AutoDispatchMode", "CreatedDate", "IdleThresholdSeconds", "LoadBalancingStrategy", "MaxConcurrentDispatches", "MinimumScoreThreshold", "Revision", "UpdatedAt", "UpdatedDate" },
            values: new object[] { 1, false, "Manual", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 30, "BestFit", 3, 0.5, 1L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

        migrationBuilder.InsertData(
            table: "MutationCounters",
            columns: new[] { "Id", "Value" },
            values: new object[] { 1, 0L });

        migrationBuilder.InsertData(
            table: "OutboxSequenceStates",
            columns: new[] { "Id", "NextSequence" },
            values: new object[] { 1, 0L });

        migrationBuilder.InsertData(
            table: "PasswordPolicies",
            columns: new[] { "Id", "MinLength", "RequireDigit", "RequireLowercase", "RequireSymbol", "RequireUppercase", "UpdatedAt" },
            values: new object[] { 1, 8, false, false, false, false, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });

        migrationBuilder.CreateIndex(
            name: "IX_ApiKeys_KeyHash",
            table: "ApiKeys",
            column: "KeyHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ApiKeys_UserId",
            table: "ApiKeys",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_ApiKeys_UserId_IsActive",
            table: "ApiKeys",
            columns: new[] { "UserId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_AppSettingsEntities_Key",
            table: "AppSettingsEntities",
            column: "Key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AttentionSnoozes_SnoozedUntilUtc",
            table: "AttentionSnoozes",
            column: "SnoozedUntilUtc");

        migrationBuilder.CreateIndex(
            name: "IX_AttentionSnoozes_UserId_AttentionItemId",
            table: "AttentionSnoozes",
            columns: new[] { "UserId", "AttentionItemId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AuthAuditLogs_EventType",
            table: "AuthAuditLogs",
            column: "EventType");

        migrationBuilder.CreateIndex(
            name: "IX_AuthAuditLogs_Success",
            table: "AuthAuditLogs",
            column: "Success");

        migrationBuilder.CreateIndex(
            name: "IX_AuthAuditLogs_Timestamp",
            table: "AuthAuditLogs",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_AuthAuditLogs_UserId",
            table: "AuthAuditLogs",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AuthAuditLogs_UserId_Timestamp",
            table: "AuthAuditLogs",
            columns: new[] { "UserId", "Timestamp" });

        migrationBuilder.CreateIndex(
            name: "IX_BalanceTransactions_CreatedAt",
            table: "BalanceTransactions",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_BalanceTransactions_PrintJobId",
            table: "BalanceTransactions",
            column: "PrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_BalanceTransactions_UserBalanceId",
            table: "BalanceTransactions",
            column: "UserBalanceId");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_Action",
            table: "BarcodeScanLogs",
            column: "Action");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_Barcode",
            table: "BarcodeScanLogs",
            column: "Barcode");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_BinId",
            table: "BarcodeScanLogs",
            column: "BinId");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_Outcome",
            table: "BarcodeScanLogs",
            column: "Outcome");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_PartInventoryId",
            table: "BarcodeScanLogs",
            column: "PartInventoryId");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_Timestamp",
            table: "BarcodeScanLogs",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_BedClearCommandRecords_Status_Expiry",
            table: "BedClearCommandRecords",
            columns: new[] { "Status", "ExpiresAtUtc" });

        migrationBuilder.CreateIndex(
            name: "UX_BedClearCommandRecords_Printer_Key",
            table: "BedClearCommandRecords",
            columns: new[] { "PrinterId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BedTypes_Name",
            table: "BedTypes",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Bins_Code",
            table: "Bins",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Bins_IsActive",
            table: "Bins",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationAttemptEvents_AttemptId_OperationId",
            table: "CalibrationAttemptEvents",
            columns: new[] { "AttemptId", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationAttemptEvents_AttemptId_Sequence",
            table: "CalibrationAttemptEvents",
            columns: new[] { "AttemptId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationAttemptEvents_ProjectId",
            table: "CalibrationAttemptEvents",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationAttempts_PrinterConfigurationSnapshotId",
            table: "CalibrationAttempts",
            column: "PrinterConfigurationSnapshotId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationAttempts_ProjectId_AttemptRequestId",
            table: "CalibrationAttempts",
            columns: new[] { "ProjectId", "AttemptRequestId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationAttempts_ProjectId_Sequence",
            table: "CalibrationAttempts",
            columns: new[] { "ProjectId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationBlobCleanups_CreatedAtUtc",
            table: "CalibrationBlobCleanups",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationBlobCleanups_OpaqueStorageKey",
            table: "CalibrationBlobCleanups",
            column: "OpaqueStorageKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationChanges_Id",
            table: "CalibrationChanges",
            column: "Id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationChanges_OwnerUserId_MutationId",
            table: "CalibrationChanges",
            columns: new[] { "OwnerUserId", "MutationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationChanges_OwnerUserId_Sequence",
            table: "CalibrationChanges",
            columns: new[] { "OwnerUserId", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationChanges_ProjectId",
            table: "CalibrationChanges",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationDrafts_ProjectId_StepId_DeviceLineageId",
            table: "CalibrationDrafts",
            columns: new[] { "ProjectId", "StepId", "DeviceLineageId" },
            unique: true,
            filter: "[DeletedAtUtc] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationIdempotencyRecords_ExpiresAtUtc",
            table: "CalibrationIdempotencyRecords",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationIdempotencyRecords_ProjectId",
            table: "CalibrationIdempotencyRecords",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationIdempotencyRecords_Scope_ClientId_OperationId",
            table: "CalibrationIdempotencyRecords",
            columns: new[] { "Scope", "ClientId", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationObservations_AttemptId_OperationId",
            table: "CalibrationObservations",
            columns: new[] { "AttemptId", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationObservations_AttemptId_Sequence",
            table: "CalibrationObservations",
            columns: new[] { "AttemptId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationObservations_ProjectId",
            table: "CalibrationObservations",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationOrchestrations_AttemptId",
            table: "CalibrationOrchestrations",
            column: "AttemptId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationOrchestrations_LeaseExpiresAtUtc",
            table: "CalibrationOrchestrations",
            column: "LeaseExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationOrchestrations_ProjectId",
            table: "CalibrationOrchestrations",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationOrchestrations_Status_NextRetryAtUtc",
            table: "CalibrationOrchestrations",
            columns: new[] { "Status", "NextRetryAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationPhotos_AttemptId_ClientUploadId",
            table: "CalibrationPhotos",
            columns: new[] { "AttemptId", "ClientUploadId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationPhotos_ProjectId_DeletedAtUtc",
            table: "CalibrationPhotos",
            columns: new[] { "ProjectId", "DeletedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationProjects_FilamentTypeId",
            table: "CalibrationProjects",
            column: "FilamentTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationProjects_LocalSpoolId",
            table: "CalibrationProjects",
            column: "LocalSpoolId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationProjects_OwnerUserId_CreateRequestId",
            table: "CalibrationProjects",
            columns: new[] { "OwnerUserId", "CreateRequestId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationProjects_OwnerUserId_DeletedAtUtc_UpdatedAtUtc",
            table: "CalibrationProjects",
            columns: new[] { "OwnerUserId", "DeletedAtUtc", "UpdatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationProjects_PrinterId",
            table: "CalibrationProjects",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationSyncCursors_Scope_Sequence",
            table: "CalibrationSyncCursors",
            columns: new[] { "Scope", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_Cameras_IsEnabled",
            table: "Cameras",
            column: "IsEnabled");

        migrationBuilder.CreateIndex(
            name: "IX_Cameras_Name",
            table: "Cameras",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_Cameras_PrinterId",
            table: "Cameras",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_Cameras_SortOrder",
            table: "Cameras",
            column: "SortOrder");

        migrationBuilder.CreateIndex(
            name: "IX_Cameras_Source",
            table: "Cameras",
            column: "Source");

        migrationBuilder.CreateIndex(
            name: "IX_CameraSnapshots_CameraId",
            table: "CameraSnapshots",
            column: "CameraId");

        migrationBuilder.CreateIndex(
            name: "IX_CameraSnapshots_CapturedAt",
            table: "CameraSnapshots",
            column: "CapturedAt");

        migrationBuilder.CreateIndex(
            name: "IX_CameraSnapshots_PrinterId",
            table: "CameraSnapshots",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_CameraSnapshots_PrintJobId",
            table: "CameraSnapshots",
            column: "PrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_CustomFieldDefinitions_EntityType_FieldKey",
            table: "CustomFieldDefinitions",
            columns: new[] { "EntityType", "FieldKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CustomFieldValues_DefinitionId_EntityId",
            table: "CustomFieldValues",
            columns: new[] { "DefinitionId", "EntityId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_InstallationId",
            table: "DeviceTokens",
            column: "InstallationId",
            unique: true,
            filter: "[IsActive] = 1");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_Token",
            table: "DeviceTokens",
            column: "Token");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_UserId",
            table: "DeviceTokens",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_DispatchLogs_CreatedAtUtc",
            table: "DispatchLogs",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_DispatchLogs_DispatchedAt",
            table: "DispatchLogs",
            column: "DispatchedAt");

        migrationBuilder.CreateIndex(
            name: "IX_DispatchLogs_PrinterId",
            table: "DispatchLogs",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_DispatchLogs_PrintJobId",
            table: "DispatchLogs",
            column: "PrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_ExtruderModelDefinitions_ManufacturerId",
            table: "ExtruderModelDefinitions",
            column: "ManufacturerId");

        migrationBuilder.CreateIndex(
            name: "IX_ExtruderModelDefinitions_Name",
            table: "ExtruderModelDefinitions",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_FailureDetectionIncidents_DetectedAt",
            table: "FailureDetectionIncidents",
            column: "DetectedAt");

        migrationBuilder.CreateIndex(
            name: "IX_FailureDetectionIncidents_PrinterId_DetectedAt",
            table: "FailureDetectionIncidents",
            columns: new[] { "PrinterId", "DetectedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_FilamentFallbackGroupMembers_FallbackGroupId",
            table: "FilamentFallbackGroupMembers",
            column: "FallbackGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentFallbackGroupMembers_ToolheadId",
            table: "FilamentFallbackGroupMembers",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroupMembers_GroupId_Position",
            table: "FilamentFallbackGroupMembers",
            columns: new[] { "FallbackGroupId", "Position" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroupMembers_GroupId_ToolheadId",
            table: "FilamentFallbackGroupMembers",
            columns: new[] { "FallbackGroupId", "ToolheadId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FilamentFallbackGroups_PrinterId",
            table: "FilamentFallbackGroups",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroups_PrinterId_NameNormalized",
            table: "FilamentFallbackGroups",
            columns: new[] { "PrinterId", "NameNormalized" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FilamentSwapOverrides_PrinterId_CreatedAtUtc",
            table: "FilamentSwapOverrides",
            columns: new[] { "PrinterId", "CreatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_FilamentTypePrinterModel_SupportedFilamentTypesId",
            table: "FilamentTypePrinterModel",
            column: "SupportedFilamentTypesId");

        migrationBuilder.CreateIndex(
            name: "IX_FilamentTypes_Name",
            table: "FilamentTypes",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FileHealthAudits_AuditDate",
            table: "FileHealthAudits",
            column: "AuditDate",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_FileHealthAudits_AuditType",
            table: "FileHealthAudits",
            column: "AuditType");

        migrationBuilder.CreateIndex(
            name: "IX_FileHealthAudits_AuditType_AuditDate",
            table: "FileHealthAudits",
            columns: new[] { "AuditType", "AuditDate" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "IX_FileHealthAudits_HasIssues",
            table: "FileHealthAudits",
            column: "HasIssues");

        migrationBuilder.CreateIndex(
            name: "IX_FolderNode_Path_FolderType",
            table: "FolderNode",
            columns: new[] { "Path", "FolderType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_CalibrationAttemptId",
            table: "GcodeFiles",
            column: "CalibrationAttemptId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_CalibrationOrchestrationId",
            table: "GcodeFiles",
            column: "CalibrationOrchestrationId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_FileHash",
            table: "GcodeFiles",
            column: "FileHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_FolderId",
            table: "GcodeFiles",
            column: "FolderId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_HealthStatus",
            table: "GcodeFiles",
            column: "HealthStatus");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_LastHealthCheckDate",
            table: "GcodeFiles",
            column: "LastHealthCheckDate");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PrinterGroupId",
            table: "GcodeFiles",
            column: "PrinterGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PrinterModelId",
            table: "GcodeFiles",
            column: "PrinterModelId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PromotionOperationId",
            table: "GcodeFiles",
            column: "PromotionOperationId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PromotionOperationKey",
            table: "GcodeFiles",
            column: "PromotionOperationKey",
            unique: true,
            filter: "[PromotionOperationKey] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_RequiredMaterial",
            table: "GcodeFiles",
            column: "RequiredMaterial");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_RequiredNozzleDiameter",
            table: "GcodeFiles",
            column: "RequiredNozzleDiameter");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_SourceArtifactId_ContentSha256",
            table: "GcodeFiles",
            columns: new[] { "SourceArtifactId", "ContentSha256" },
            unique: true,
            filter: "[SourceArtifactId] IS NOT NULL AND [ContentSha256] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_SourcePrinterId",
            table: "GcodeFiles",
            column: "SourcePrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_UploadedAt",
            table: "GcodeFiles",
            column: "UploadedAt");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFileTag_TagsId",
            table: "GcodeFileTag",
            column: "TagsId");

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
            name: "IX_GcodeHarvestQueueItems_PrinterId",
            table: "GcodeHarvestQueueItems",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeHarvestQueueItems_QueuedAt",
            table: "GcodeHarvestQueueItems",
            column: "QueuedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_GcodeHarvestQueueItems_Status",
            table: "GcodeHarvestQueueItems",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeHarvestQueueItems_Status_Priority_QueuedAt",
            table: "GcodeHarvestQueueItems",
            columns: new[] { "Status", "Priority", "QueuedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_GcodeFileId",
            table: "GcodePromotionCheckpoints",
            column: "GcodeFileId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_OperationScope_OperationId",
            table: "GcodePromotionCheckpoints",
            columns: new[] { "OperationScope", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_SourceArtifactId_SourceContentSha256",
            table: "GcodePromotionCheckpoints",
            columns: new[] { "SourceArtifactId", "SourceContentSha256" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_State_UpdatedAtUtc",
            table: "GcodePromotionCheckpoints",
            columns: new[] { "State", "UpdatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_GeneratedProfileRevisionOperations_GeneratedProfileRevisionId_OperationId",
            table: "GeneratedProfileRevisionOperations",
            columns: new[] { "GeneratedProfileRevisionId", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GeneratedProfileRevisions_ProjectId_GenerationRequestId",
            table: "GeneratedProfileRevisions",
            columns: new[] { "ProjectId", "GenerationRequestId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GeneratedProfileRevisions_ProjectId_RevisionNumber",
            table: "GeneratedProfileRevisions",
            columns: new[] { "ProjectId", "RevisionNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GeneratedProfileRevisions_SourceAttemptId",
            table: "GeneratedProfileRevisions",
            column: "SourceAttemptId");

        migrationBuilder.CreateIndex(
            name: "IX_HarvestDiscoveredFiles_HarvestOperationId",
            table: "HarvestDiscoveredFiles",
            column: "HarvestOperationId");

        migrationBuilder.CreateIndex(
            name: "IX_HarvestFileGcodeFileMappings_CreatedAt",
            table: "HarvestFileGcodeFileMappings",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_HarvestFileGcodeFileMappings_GcodeFileId",
            table: "HarvestFileGcodeFileMappings",
            column: "GcodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_HarvestFileGcodeFileMappings_HarvestDiscoveredFileId",
            table: "HarvestFileGcodeFileMappings",
            column: "HarvestDiscoveredFileId");

        migrationBuilder.CreateIndex(
            name: "IX_HotendModelDefinitions_ManufacturerId",
            table: "HotendModelDefinitions",
            column: "ManufacturerId");

        migrationBuilder.CreateIndex(
            name: "IX_HotendModelDefinitions_Name",
            table: "HotendModelDefinitions",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_CreatedAt",
            table: "IdempotencyRecords",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords",
            columns: new[] { "UserId", "RouteKey", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_JobExecutions_JobScheduleId_ScheduledExecutionTime",
            table: "JobExecutions",
            columns: new[] { "JobScheduleId", "ScheduledExecutionTime" });

        migrationBuilder.CreateIndex(
            name: "IX_JobExecutions_OccurrencePrintJobId",
            table: "JobExecutions",
            column: "OccurrencePrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_JobExecutions_ScheduledExecutionTime",
            table: "JobExecutions",
            column: "ScheduledExecutionTime");

        migrationBuilder.CreateIndex(
            name: "IX_JobExecutions_Status",
            table: "JobExecutions",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_JobRetries_OriginalJobId",
            table: "JobRetries",
            column: "OriginalJobId");

        migrationBuilder.CreateIndex(
            name: "IX_JobRetries_OriginalJobId_AttemptNumber",
            table: "JobRetries",
            columns: new[] { "OriginalJobId", "AttemptNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_JobRetries_RetryJobId",
            table: "JobRetries",
            column: "RetryJobId");

        migrationBuilder.CreateIndex(
            name: "IX_JobRetries_ScheduledRetryTime",
            table: "JobRetries",
            column: "ScheduledRetryTime");

        migrationBuilder.CreateIndex(
            name: "IX_JobRetries_Status",
            table: "JobRetries",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_JobSchedules_IsActive",
            table: "JobSchedules",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_JobSchedules_IsActive_IsPaused",
            table: "JobSchedules",
            columns: new[] { "IsActive", "IsPaused" });

        migrationBuilder.CreateIndex(
            name: "IX_JobSchedules_PrintJobId",
            table: "JobSchedules",
            column: "PrintJobId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_JobSchedules_RootPrintJobId",
            table: "JobSchedules",
            column: "RootPrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_JobSchedules_ScheduledStartTime",
            table: "JobSchedules",
            column: "ScheduledStartTime");

        migrationBuilder.CreateIndex(
            name: "IX_JobStateHistories_JobId",
            table: "JobStateHistories",
            column: "JobId");

        migrationBuilder.CreateIndex(
            name: "IX_JobStateHistories_TransitionedAtUtc",
            table: "JobStateHistories",
            column: "TransitionedAtUtc",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_LibrarySyncChanges_EntityType_EntityId",
            table: "LibrarySyncChanges",
            columns: new[] { "EntityType", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_LibrarySyncChanges_OwnerUserId",
            table: "LibrarySyncChanges",
            column: "OwnerUserId");

        migrationBuilder.CreateIndex(
            name: "IX_LibrarySyncChanges_Timestamp",
            table: "LibrarySyncChanges",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_Locations_CreatedAt",
            table: "Locations",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Locations_IsActive",
            table: "Locations",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_Locations_ParentId",
            table: "Locations",
            column: "ParentId");

        migrationBuilder.CreateIndex(
            name: "IX_Locations_ParentId_Name",
            table: "Locations",
            columns: new[] { "ParentId", "Name" },
            unique: true,
            filter: "[ParentId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Locations_Path",
            table: "Locations",
            column: "Path");

        migrationBuilder.CreateIndex(
            name: "IX_LoginAuditEntries_Success",
            table: "LoginAuditEntries",
            column: "Success");

        migrationBuilder.CreateIndex(
            name: "IX_LoginAuditEntries_Timestamp",
            table: "LoginAuditEntries",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_LoginAuditEntries_Username",
            table: "LoginAuditEntries",
            column: "Username");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_CreatedAt",
            table: "MaintenanceAlerts",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_MaintenanceTaskId",
            table: "MaintenanceAlerts",
            column: "MaintenanceTaskId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_PrinterId",
            table: "MaintenanceAlerts",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_PrinterMaintenanceScheduleId",
            table: "MaintenanceAlerts",
            column: "PrinterMaintenanceScheduleId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_Status_Severity",
            table: "MaintenanceAlerts",
            columns: new[] { "Status", "Severity" });

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_ToolheadId",
            table: "MaintenanceAlerts",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceComponents_Category",
            table: "MaintenanceComponents",
            column: "Category");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceComponents_Name",
            table: "MaintenanceComponents",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_MaintenanceTaskId",
            table: "MaintenanceLogs",
            column: "MaintenanceTaskId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_PerformedAt",
            table: "MaintenanceLogs",
            column: "PerformedAt");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_PrinterId",
            table: "MaintenanceLogs",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_PrinterMaintenanceScheduleId",
            table: "MaintenanceLogs",
            column: "PrinterMaintenanceScheduleId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            unique: true,
            filter: "[ResolvedAlertId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_ToolheadId",
            table: "MaintenanceLogs",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenancePlans_IsActive",
            table: "MaintenancePlans",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenancePlans_ManufacturerId",
            table: "MaintenancePlans",
            column: "ManufacturerId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenancePlans_PrinterId",
            table: "MaintenancePlans",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenancePlans_PrinterModelId",
            table: "MaintenancePlans",
            column: "PrinterModelId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceTaskComponents_MaintenanceComponentId",
            table: "MaintenanceTaskComponents",
            column: "MaintenanceComponentId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceTaskComponents_MaintenanceTaskId_MaintenanceComponentId",
            table: "MaintenanceTaskComponents",
            columns: new[] { "MaintenanceTaskId", "MaintenanceComponentId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceTasks_Category",
            table: "MaintenanceTasks",
            column: "Category");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceTasks_IsActive",
            table: "MaintenanceTasks",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceTasks_TaskName",
            table: "MaintenanceTasks",
            column: "TaskName");

        migrationBuilder.CreateIndex(
            name: "IX_Manufacturers_NameLowered",
            table: "Manufacturers",
            column: "NameLowered",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MaterialClusterMembers_FilamentTypeId",
            table: "MaterialClusterMembers",
            column: "FilamentTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_MaterialClusters_Name",
            table: "MaterialClusters",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Model3DTag_TagsId",
            table: "Model3DTag",
            column: "TagsId");

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollectionMemberships_CollectionId_ModelId",
            table: "ModelCollectionMemberships",
            columns: new[] { "CollectionId", "ModelId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollectionMemberships_ModelId",
            table: "ModelCollectionMemberships",
            column: "ModelId");

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollectionMemberships_UpdatedAt",
            table: "ModelCollectionMemberships",
            column: "UpdatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollections_OwnerUserId",
            table: "ModelCollections",
            column: "OwnerUserId");

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollections_UpdatedAt",
            table: "ModelCollections",
            column: "UpdatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_NfcDevices_PrinterId",
            table: "NfcDevices",
            column: "PrinterId",
            unique: true,
            filter: "\"PrinterId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_NfcScanEvents_NfcDeviceId",
            table: "NfcScanEvents",
            column: "NfcDeviceId");

        migrationBuilder.CreateIndex(
            name: "IX_NfcScanEvents_ScannedAt",
            table: "NfcScanEvents",
            column: "ScannedAt");

        migrationBuilder.CreateIndex(
            name: "IX_NfcTagBindings_PrinterId",
            table: "NfcTagBindings",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_NfcTagBindings_SpoolId",
            table: "NfcTagBindings",
            column: "SpoolId");

        migrationBuilder.CreateIndex(
            name: "IX_NfcTagBindings_TagUid",
            table: "NfcTagBindings",
            column: "TagUid",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_NotificationPreferences_UserId",
            table: "NotificationPreferences",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_CreatedAt",
            table: "Notifications",
            column: "CreatedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_ExpiresAt",
            table: "Notifications",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_JobId",
            table: "Notifications",
            column: "JobId");

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_Type",
            table: "Notifications",
            column: "Type");

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_UserId",
            table: "Notifications",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Notifications_UserId_IsRead",
            table: "Notifications",
            columns: new[] { "UserId", "IsRead" });

        migrationBuilder.CreateIndex(
            name: "IX_NozzleModelDefinitions_ManufacturerId",
            table: "NozzleModelDefinitions",
            column: "ManufacturerId");

        migrationBuilder.CreateIndex(
            name: "IX_NozzleModelDefinitions_Name",
            table: "NozzleModelDefinitions",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_ActualBinId",
            table: "PartHarvestOutputSnapshots",
            column: "ActualBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_ExpectedBinId",
            table: "PartHarvestOutputSnapshots",
            column: "ExpectedBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_PartInventoryAdjustmentId",
            table: "PartHarvestOutputSnapshots",
            column: "PartInventoryAdjustmentId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_PartInventoryId",
            table: "PartHarvestOutputSnapshots",
            column: "PartInventoryId");

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_PrintJobId_Sequence",
            table: "PartHarvestOutputSnapshots",
            columns: new[] { "PrintJobId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_SourceMappingId",
            table: "PartHarvestOutputSnapshots",
            column: "SourceMappingId");

        migrationBuilder.CreateIndex(
            name: "IX_PartInventories_DefaultBinId",
            table: "PartInventories",
            column: "DefaultBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PartInventories_IsActive",
            table: "PartInventories",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_PartInventories_Sku",
            table: "PartInventories",
            column: "Sku",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PartInventoryAdjustments_BinId",
            table: "PartInventoryAdjustments",
            column: "BinId");

        migrationBuilder.CreateIndex(
            name: "IX_PartInventoryAdjustments_PartInventoryId",
            table: "PartInventoryAdjustments",
            column: "PartInventoryId");

        migrationBuilder.CreateIndex(
            name: "IX_PartInventoryAdjustments_PartInventoryId_CreatedAt",
            table: "PartInventoryAdjustments",
            columns: new[] { "PartInventoryId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_PartInventoryAdjustments_PartInventoryId_OperationKey",
            table: "PartInventoryAdjustments",
            columns: new[] { "PartInventoryId", "OperationKey" },
            unique: true,
            filter: "[OperationKey] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PartInventoryAdjustments_PrintJobId",
            table: "PartInventoryAdjustments",
            column: "PrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_PartInventoryAdjustments_Reason",
            table: "PartInventoryAdjustments",
            column: "Reason");

        migrationBuilder.CreateIndex(
            name: "IX_PartOutputMappings_GcodeFileId",
            table: "PartOutputMappings",
            column: "GcodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_PartOutputMappings_GcodeFileId_PartInventoryId",
            table: "PartOutputMappings",
            columns: new[] { "GcodeFileId", "PartInventoryId" },
            unique: true,
            filter: "[GcodeFileId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PartOutputMappings_PartInventoryId",
            table: "PartOutputMappings",
            column: "PartInventoryId");

        migrationBuilder.CreateIndex(
            name: "IX_PartOutputMappings_PrintProjectFileId",
            table: "PartOutputMappings",
            column: "PrintProjectFileId");

        migrationBuilder.CreateIndex(
            name: "IX_PartOutputMappings_PrintProjectFileId_PartInventoryId",
            table: "PartOutputMappings",
            columns: new[] { "PrintProjectFileId", "PartInventoryId" },
            unique: true,
            filter: "[PrintProjectFileId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PasswordResetTokens_ExpiresAt",
            table: "PasswordResetTokens",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_PasswordResetTokens_IsUsed",
            table: "PasswordResetTokens",
            column: "IsUsed");

        migrationBuilder.CreateIndex(
            name: "IX_PasswordResetTokens_Token",
            table: "PasswordResetTokens",
            column: "Token",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PasswordResetTokens_UserId",
            table: "PasswordResetTokens",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_PlanTasks_MaintenancePlanId_MaintenanceTaskId",
            table: "PlanTasks",
            columns: new[] { "MaintenancePlanId", "MaintenanceTaskId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PlanTasks_MaintenanceTaskId",
            table: "PlanTasks",
            column: "MaintenanceTaskId");

        migrationBuilder.CreateIndex(
            name: "IX_PowerMonitors_PrinterId",
            table: "PowerMonitors",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PowerReadings_PowerMonitorId",
            table: "PowerReadings",
            column: "PowerMonitorId");

        migrationBuilder.CreateIndex(
            name: "IX_PowerReadings_RecordedAt",
            table: "PowerReadings",
            column: "RecordedAt");

        migrationBuilder.CreateIndex(
            name: "IX_PrintApprovals_CreatedAt",
            table: "PrintApprovals",
            column: "CreatedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_PrintApprovals_PrintJobId",
            table: "PrintApprovals",
            column: "PrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterConfigurationSnapshots_AttemptId",
            table: "PrinterConfigurationSnapshots",
            column: "AttemptId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterConfigurationSnapshots_ProjectId_SnapshotSha256",
            table: "PrinterConfigurationSnapshots",
            columns: new[] { "ProjectId", "SnapshotSha256" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterGroupAccesses_PrinterGroupId_RoleId_AccessLevel",
            table: "PrinterGroupAccesses",
            columns: new[] { "PrinterGroupId", "RoleId", "AccessLevel" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterGroupAccesses_RoleId",
            table: "PrinterGroupAccesses",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterGroups_Name",
            table: "PrinterGroups",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterMaintenanceSchedules_IsActive",
            table: "PrinterMaintenanceSchedules",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterMaintenanceSchedules_PrinterId",
            table: "PrinterMaintenanceSchedules",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterMaintenanceSchedules_ToolheadId",
            table: "PrinterMaintenanceSchedules",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_NullToolhead",
            table: "PrinterMaintenanceSchedules",
            columns: new[] { "MaintenancePlanId", "PrinterId" },
            unique: true,
            filter: "\"ToolheadId\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_Toolhead",
            table: "PrinterMaintenanceSchedules",
            columns: new[] { "MaintenancePlanId", "PrinterId", "ToolheadId" },
            unique: true,
            filter: "[ToolheadId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelName_SlicerType",
            table: "PrinterModelAliases",
            columns: new[] { "PrinterModelId", "SlicerModelName", "SlicerType" },
            unique: true,
            filter: "[SlicerType] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModels_DefaultBedTypeId",
            table: "PrinterModels",
            column: "DefaultBedTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModels_ManufacturerId_NameLowered",
            table: "PrinterModels",
            columns: new[] { "ManufacturerId", "NameLowered" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelToolheads_ExtruderModelId",
            table: "PrinterModelToolheads",
            column: "ExtruderModelId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelToolheads_HotendModelId",
            table: "PrinterModelToolheads",
            column: "HotendModelId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelToolheads_Index",
            table: "PrinterModelToolheads",
            column: "Index");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelToolheads_NozzleModelId",
            table: "PrinterModelToolheads",
            column: "NozzleModelId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelToolheads_PrinterModelId",
            table: "PrinterModelToolheads",
            column: "PrinterModelId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelToolheads_ToolheadModelDefId",
            table: "PrinterModelToolheads",
            column: "ToolheadModelDefId");

        migrationBuilder.CreateIndex(
            name: "IX_Printers_BedTypeId",
            table: "Printers",
            column: "BedTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_Printers_LocationId",
            table: "Printers",
            column: "LocationId");

        migrationBuilder.CreateIndex(
            name: "IX_Printers_ManufacturerId",
            table: "Printers",
            column: "ManufacturerId");

        migrationBuilder.CreateIndex(
            name: "IX_Printers_ModelId",
            table: "Printers",
            column: "ModelId");

        migrationBuilder.CreateIndex(
            name: "IX_Printers_PrinterGroupId",
            table: "Printers",
            column: "PrinterGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_Printers_ServerUrl",
            table: "Printers",
            column: "ServerUrl",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterServiceState_ObicoServerId",
            table: "PrinterServiceState",
            column: "ObicoServerId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterStatisticsSet_LastSyncTime",
            table: "PrinterStatisticsSet",
            column: "LastSyncTime");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterStatisticsSet_PrinterId",
            table: "PrinterStatisticsSet",
            column: "PrinterId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterTag_TagsId",
            table: "PrinterTag",
            column: "TagsId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_ExpectedBinId",
            table: "PrintJobPartOutputSnapshots",
            column: "ExpectedBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_PartInventoryId",
            table: "PrintJobPartOutputSnapshots",
            column: "PartInventoryId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_PrintJobId_Sequence",
            table: "PrintJobPartOutputSnapshots",
            columns: new[] { "PrintJobId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_SourceMappingId",
            table: "PrintJobPartOutputSnapshots",
            column: "SourceMappingId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_AssignedPrinterId",
            table: "PrintJobs",
            column: "AssignedPrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_AssignedPrinterId_Status",
            table: "PrintJobs",
            columns: new[] { "AssignedPrinterId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_DeadlineAtUtc",
            table: "PrintJobs",
            column: "DeadlineAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_ExternalJobId_SourcePrinterId",
            table: "PrintJobs",
            columns: new[] { "ExternalJobId", "SourcePrinterId" },
            unique: true,
            filter: "[ExternalJobId] IS NOT NULL AND [SourcePrinterId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_GcodeFileId",
            table: "PrintJobs",
            column: "GcodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_HarvestedAt",
            table: "PrintJobs",
            column: "HarvestedAt");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_HarvestedIntoBinId",
            table: "PrintJobs",
            column: "HarvestedIntoBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_HarvestOperationKey",
            table: "PrintJobs",
            column: "HarvestOperationKey",
            unique: true,
            filter: "[HarvestOperationKey] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_Idempotency_Calibration",
            table: "PrintJobs",
            columns: new[] { "IdempotencyScope", "IdempotencyKey" },
            unique: true,
            filter: "[IdempotencyScope] IS NOT NULL AND [IdempotencyKey] IS NOT NULL AND [JobKind] = 1");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_Priority",
            table: "PrintJobs",
            column: "Priority");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_QueuedAt",
            table: "PrintJobs",
            column: "QueuedAt");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_SourcePrinterId",
            table: "PrintJobs",
            column: "SourcePrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_Status",
            table: "PrintJobs",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "UX_PrintJobs_ActiveExternalPrinterId",
            table: "PrintJobs",
            column: "ActiveExternalPrinterId",
            unique: true,
            filter: "[ActiveExternalPrinterId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "UX_PrintJobs_Printer_QueuePosition",
            table: "PrintJobs",
            columns: new[] { "AssignedPrinterId", "QueuePosition" },
            unique: true,
            filter: "[AssignedPrinterId] IS NOT NULL AND [Status] IN (0, 1)");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobStatistics_CompletedAtUtc",
            table: "PrintJobStatistics",
            column: "CompletedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobStatistics_IsSuccess",
            table: "PrintJobStatistics",
            column: "IsSuccess");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobStatistics_PrinterModelId_Material_CompletedAtUtc",
            table: "PrintJobStatistics",
            columns: new[] { "PrinterModelId", "Material", "CompletedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobStatistics_PrinterModelId_Material_IsSuccess",
            table: "PrintJobStatistics",
            columns: new[] { "PrinterModelId", "Material", "IsSuccess" });

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobStatistics_PrintJobId",
            table: "PrintJobStatistics",
            column: "PrintJobId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobTag_TagsId",
            table: "PrintJobTag",
            column: "TagsId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobToolheadUsages_PrintJobId_ToolheadIndex",
            table: "PrintJobToolheadUsages",
            columns: new[] { "PrintJobId", "ToolheadIndex" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobToolheadUsages_SpoolProjection",
            table: "PrintJobToolheadUsages",
            columns: new[] { "SpoolSourceKind", "SpoolSourceIdentity", "SpoolmanSpoolId", "IsFilamentUsageAuthoritative" });

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_GcodeFileId",
            table: "PrintProjectFiles",
            column: "GcodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_LastPrintJobId",
            table: "PrintProjectFiles",
            column: "LastPrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_PrintProjectId",
            table: "PrintProjectFiles",
            column: "PrintProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId_PlateIndex",
            table: "PrintProjectFiles",
            columns: new[] { "PrintProjectId", "GcodeFileId", "PlateIndex" },
            unique: true,
            filter: "[PlateIndex] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_SpoolmanFilamentId",
            table: "PrintProjectFiles",
            column: "SpoolmanFilamentId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_Status",
            table: "PrintProjectFiles",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjects_CreatedAt",
            table: "PrintProjects",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjects_DueDate",
            table: "PrintProjects",
            column: "DueDate");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjects_Priority",
            table: "PrintProjects",
            column: "Priority");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjects_Status",
            table: "PrintProjects",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectTemplateFiles_PrintProjectTemplateId",
            table: "PrintProjectTemplateFiles",
            column: "PrintProjectTemplateId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectTemplateFiles_SortOrder",
            table: "PrintProjectTemplateFiles",
            column: "SortOrder");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectTemplates_Category",
            table: "PrintProjectTemplates",
            column: "Category");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectTemplates_Name",
            table: "PrintProjectTemplates",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectTemplates_SortOrder",
            table: "PrintProjectTemplates",
            column: "SortOrder");

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_GroupName",
            table: "PrintQuotas",
            column: "GroupName");

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_IsActive",
            table: "PrintQuotas",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_ResetAt",
            table: "PrintQuotas",
            column: "ResetAt");

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_UserId",
            table: "PrintQuotas",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_PushSubscriptions_UserId_Endpoint",
            table: "PushSubscriptions",
            columns: new[] { "UserId", "Endpoint" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_QueueDispatchAttempts_Job_Attempt",
            table: "QueueDispatchAttempts",
            columns: new[] { "PrintJobId", "AttemptNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_QueueDispatchAttempts_Printer_Outcome",
            table: "QueueDispatchAttempts",
            columns: new[] { "PrinterId", "Outcome" });

        migrationBuilder.CreateIndex(
            name: "IX_QueueDispatchOutbox_Status_RetryAfterUtc",
            table: "QueueDispatchOutbox",
            columns: new[] { "Status", "RetryAfterUtc" });

        migrationBuilder.CreateIndex(
            name: "UX_QueueDispatchOutbox_Sequence",
            table: "QueueDispatchOutbox",
            column: "Sequence",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_QueueOperationAudits_OccurredAt",
            table: "QueueOperationAudits",
            column: "OccurredAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_QueueOperationAudits_Printer_OccurredAt",
            table: "QueueOperationAudits",
            columns: new[] { "PrinterId", "OccurredAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_QueueOperationAudits_Resource",
            table: "QueueOperationAudits",
            columns: new[] { "ResourceType", "ResourceId" });

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_ExpiresAt",
            table: "RefreshTokens",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_IsRevoked",
            table: "RefreshTokens",
            column: "IsRevoked");

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_Token",
            table: "RefreshTokens",
            column: "Token",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_UserId",
            table: "RefreshTokens",
            column: "UserId");

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
            name: "IX_RevokedTokens_ExpiresAt",
            table: "RevokedTokens",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_RevokedTokens_RevokedAt",
            table: "RevokedTokens",
            column: "RevokedAt");

        migrationBuilder.CreateIndex(
            name: "IX_RevokedTokens_RevokedByUserId",
            table: "RevokedTokens",
            column: "RevokedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_RevokedTokens_TokenHash",
            table: "RevokedTokens",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RevokedTokens_UserId",
            table: "RevokedTokens",
            column: "UserId");

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
            name: "IX_SystemLogs_Level",
            table: "SystemLogs",
            column: "Level");

        migrationBuilder.CreateIndex(
            name: "IX_SystemLogs_Timestamp",
            table: "SystemLogs",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_Tags_Category_IsAutoGenerated",
            table: "Tags",
            columns: new[] { "Category", "IsAutoGenerated" });

        migrationBuilder.CreateIndex(
            name: "IX_Tags_CreatedAt",
            table: "Tags",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Tags_Name_Category",
            table: "Tags",
            columns: new[] { "Name", "Category" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ToolheadModelDefinitions_DefaultExtruderId",
            table: "ToolheadModelDefinitions",
            column: "DefaultExtruderId");

        migrationBuilder.CreateIndex(
            name: "IX_ToolheadModelDefinitions_DefaultHotendId",
            table: "ToolheadModelDefinitions",
            column: "DefaultHotendId");

        migrationBuilder.CreateIndex(
            name: "IX_ToolheadModelDefinitions_DefaultNozzleId",
            table: "ToolheadModelDefinitions",
            column: "DefaultNozzleId");

        migrationBuilder.CreateIndex(
            name: "IX_ToolheadModelDefinitions_ManufacturerId",
            table: "ToolheadModelDefinitions",
            column: "ManufacturerId");

        migrationBuilder.CreateIndex(
            name: "IX_ToolheadModelDefinitions_Name",
            table: "ToolheadModelDefinitions",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_Toolheads_CurrentSpoolId",
            table: "Toolheads",
            column: "CurrentSpoolId");

        migrationBuilder.CreateIndex(
            name: "IX_Toolheads_ExtruderModelId",
            table: "Toolheads",
            column: "ExtruderModelId");

        migrationBuilder.CreateIndex(
            name: "IX_Toolheads_HotendModelId",
            table: "Toolheads",
            column: "HotendModelId");

        migrationBuilder.CreateIndex(
            name: "IX_Toolheads_Index",
            table: "Toolheads",
            column: "Index");

        migrationBuilder.CreateIndex(
            name: "IX_Toolheads_NozzleModelId",
            table: "Toolheads",
            column: "NozzleModelId");

        migrationBuilder.CreateIndex(
            name: "IX_Toolheads_PrinterId",
            table: "Toolheads",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_Toolheads_ToolheadModelDefId",
            table: "Toolheads",
            column: "ToolheadModelDefId");

        migrationBuilder.CreateIndex(
            name: "UX_Toolheads_PrinterId_Index",
            table: "Toolheads",
            columns: new[] { "PrinterId", "Index" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserActions_Name",
            table: "UserActions",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserBalances_UserId",
            table: "UserBalances",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserPasskeyCredentials_CredentialId",
            table: "UserPasskeyCredentials",
            column: "CredentialId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserPasskeyCredentials_UserId",
            table: "UserPasskeyCredentials",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_UserQuotaGroupMemberships_GroupName",
            table: "UserQuotaGroupMemberships",
            column: "GroupName");

        migrationBuilder.CreateIndex(
            name: "IX_UserQuotaGroupMemberships_UserId_GroupName",
            table: "UserQuotaGroupMemberships",
            columns: new[] { "UserId", "GroupName" },
            unique: true);

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

        migrationBuilder.CreateIndex(
            name: "IX_UserSettings_UserId",
            table: "UserSettings",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_DismissedByUserId",
            table: "UserTasks",
            column: "DismissedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_OpenProfileImport",
            table: "UserTasks",
            columns: new[] { "TaskType", "EntityType", "EntityId" },
            unique: true,
            filter: "[TaskType] = 1 AND [EntityType] = 'PrinterModel' AND [Status] IN (0, 1)");

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_SourceKind_SourceId",
            table: "UserTasks",
            columns: new[] { "SourceKind", "SourceId" },
            unique: true,
            filter: "[SourceId] IS NOT NULL AND [Status] IN (0, 1)");

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_Status_AnchorKind_AnchorAtUtc",
            table: "UserTasks",
            columns: new[] { "Status", "AnchorKind", "AnchorAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_Status_SourceKind_SourceId",
            table: "UserTasks",
            columns: new[] { "Status", "SourceKind", "SourceId" });

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_Status_UpdatedAt",
            table: "UserTasks",
            columns: new[] { "Status", "UpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_CreatedAt",
            table: "WebhookDeliveryLogs",
            column: "CreatedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_EventType",
            table: "WebhookDeliveryLogs",
            column: "EventType");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_Success",
            table: "WebhookDeliveryLogs",
            column: "Success");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_WebhookSubscriptionId",
            table: "WebhookDeliveryLogs",
            column: "WebhookSubscriptionId");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookSubscriptions_CreatedAt",
            table: "WebhookSubscriptions",
            column: "CreatedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_WebhookSubscriptions_IsActive",
            table: "WebhookSubscriptions",
            column: "IsActive");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ApiKeys");

        migrationBuilder.DropTable(
            name: "AppSettingsEntities");

        migrationBuilder.DropTable(
            name: "AttentionSnoozes");

        migrationBuilder.DropTable(
            name: "AuthAuditLogs");

        migrationBuilder.DropTable(
            name: "BalanceTransactions");

        migrationBuilder.DropTable(
            name: "BarcodeScanLogs");

        migrationBuilder.DropTable(
            name: "BedClearCommandRecords");

        migrationBuilder.DropTable(
            name: "CalibrationAttemptEvents");

        migrationBuilder.DropTable(
            name: "CalibrationBlobCleanups");

        migrationBuilder.DropTable(
            name: "CalibrationChangeFeedStates");

        migrationBuilder.DropTable(
            name: "CalibrationChanges");

        migrationBuilder.DropTable(
            name: "CalibrationDrafts");

        migrationBuilder.DropTable(
            name: "CalibrationIdempotencyRecords");

        migrationBuilder.DropTable(
            name: "CalibrationObservations");

        migrationBuilder.DropTable(
            name: "CalibrationOrchestrations");

        migrationBuilder.DropTable(
            name: "CalibrationPhotos");

        migrationBuilder.DropTable(
            name: "CalibrationSyncCursors");

        migrationBuilder.DropTable(
            name: "CameraSnapshots");

        migrationBuilder.DropTable(
            name: "CatalogVersions");

        migrationBuilder.DropTable(
            name: "CustomFieldValues");

        migrationBuilder.DropTable(
            name: "DeviceTokens");

        migrationBuilder.DropTable(
            name: "DispatchLogs");

        migrationBuilder.DropTable(
            name: "DispatchSettings");

        migrationBuilder.DropTable(
            name: "FailedLoginAttempts");

        migrationBuilder.DropTable(
            name: "FailureDetectionIncidents");

        migrationBuilder.DropTable(
            name: "FilamentFallbackGroupMembers");

        migrationBuilder.DropTable(
            name: "FilamentSwapOverrides");

        migrationBuilder.DropTable(
            name: "FilamentTypePrinterModel");

        migrationBuilder.DropTable(
            name: "FileHealthAudits");

        migrationBuilder.DropTable(
            name: "GcodeFileTag");

        migrationBuilder.DropTable(
            name: "GcodeHarvestQueueItems");

        migrationBuilder.DropTable(
            name: "GcodePromotionCheckpoints");

        migrationBuilder.DropTable(
            name: "GeneratedProfileRevisionOperations");

        migrationBuilder.DropTable(
            name: "HarvestFileGcodeFileMappings");

        migrationBuilder.DropTable(
            name: "IdempotencyRecords");

        migrationBuilder.DropTable(
            name: "JobExecutions");

        migrationBuilder.DropTable(
            name: "JobRetries");

        migrationBuilder.DropTable(
            name: "JobStateHistories");

        migrationBuilder.DropTable(
            name: "LibrarySyncChanges");

        migrationBuilder.DropTable(
            name: "LoginAuditEntries");

        migrationBuilder.DropTable(
            name: "MaintenanceLogs");

        migrationBuilder.DropTable(
            name: "MaintenanceTaskComponents");

        migrationBuilder.DropTable(
            name: "MaterialClusterMembers");

        migrationBuilder.DropTable(
            name: "Model3DTag");

        migrationBuilder.DropTable(
            name: "ModelCollectionMemberships");

        migrationBuilder.DropTable(
            name: "MutationCounters");

        migrationBuilder.DropTable(
            name: "NfcScanEvents");

        migrationBuilder.DropTable(
            name: "NfcTagBindings");

        migrationBuilder.DropTable(
            name: "NotificationPreferences");

        migrationBuilder.DropTable(
            name: "Notifications");

        migrationBuilder.DropTable(
            name: "OutboxSequenceStates");

        migrationBuilder.DropTable(
            name: "PartHarvestOutputSnapshots");

        migrationBuilder.DropTable(
            name: "PartOutputMappings");

        migrationBuilder.DropTable(
            name: "PasswordPolicies");

        migrationBuilder.DropTable(
            name: "PasswordResetTokens");

        migrationBuilder.DropTable(
            name: "PlanTasks");

        migrationBuilder.DropTable(
            name: "PowerReadings");

        migrationBuilder.DropTable(
            name: "PrintApprovals");

        migrationBuilder.DropTable(
            name: "PrinterDispatchStates");

        migrationBuilder.DropTable(
            name: "PrinterGroupAccesses");

        migrationBuilder.DropTable(
            name: "PrinterModelAliases");

        migrationBuilder.DropTable(
            name: "PrinterModelToolheads");

        migrationBuilder.DropTable(
            name: "PrinterServiceState");

        migrationBuilder.DropTable(
            name: "PrinterStatisticsSet");

        migrationBuilder.DropTable(
            name: "PrinterTag");

        migrationBuilder.DropTable(
            name: "PrintJobPartOutputSnapshots");

        migrationBuilder.DropTable(
            name: "PrintJobStatistics");

        migrationBuilder.DropTable(
            name: "PrintJobTag");

        migrationBuilder.DropTable(
            name: "PrintJobToolheadUsages");

        migrationBuilder.DropTable(
            name: "PrintProjectTemplateFiles");

        migrationBuilder.DropTable(
            name: "PrintQuotas");

        migrationBuilder.DropTable(
            name: "PushSubscriptions");

        migrationBuilder.DropTable(
            name: "QueueDispatchAttempts");

        migrationBuilder.DropTable(
            name: "QueueDispatchOutbox");

        migrationBuilder.DropTable(
            name: "QueueOperationAudits");

        migrationBuilder.DropTable(
            name: "QueuePositionStates");

        migrationBuilder.DropTable(
            name: "RefreshTokens");

        migrationBuilder.DropTable(
            name: "RetryPolicies");

        migrationBuilder.DropTable(
            name: "RevokedTokens");

        migrationBuilder.DropTable(
            name: "RolePermissions");

        migrationBuilder.DropTable(
            name: "SpoolmanConfigs");

        migrationBuilder.DropTable(
            name: "SystemLogs");

        migrationBuilder.DropTable(
            name: "UserPasskeyCredentials");

        migrationBuilder.DropTable(
            name: "UserQuotaGroupMemberships");

        migrationBuilder.DropTable(
            name: "UserRoles");

        migrationBuilder.DropTable(
            name: "UserSettings");

        migrationBuilder.DropTable(
            name: "UserTasks");

        migrationBuilder.DropTable(
            name: "WebhookDeliveryLogs");

        migrationBuilder.DropTable(
            name: "UserBalances");

        migrationBuilder.DropTable(
            name: "Cameras");

        migrationBuilder.DropTable(
            name: "CustomFieldDefinitions");

        migrationBuilder.DropTable(
            name: "FilamentFallbackGroups");

        migrationBuilder.DropTable(
            name: "GeneratedProfileRevisions");

        migrationBuilder.DropTable(
            name: "HarvestDiscoveredFiles");

        migrationBuilder.DropTable(
            name: "JobSchedules");

        migrationBuilder.DropTable(
            name: "MaintenanceAlerts");

        migrationBuilder.DropTable(
            name: "MaintenanceComponents");

        migrationBuilder.DropTable(
            name: "MaterialClusters");

        migrationBuilder.DropTable(
            name: "ModelCollections");

        migrationBuilder.DropTable(
            name: "NfcDevices");

        migrationBuilder.DropTable(
            name: "PartInventoryAdjustments");

        migrationBuilder.DropTable(
            name: "PrintProjectFiles");

        migrationBuilder.DropTable(
            name: "PowerMonitors");

        migrationBuilder.DropTable(
            name: "ObicoServers");

        migrationBuilder.DropTable(
            name: "Tags");

        migrationBuilder.DropTable(
            name: "PrintProjectTemplates");

        migrationBuilder.DropTable(
            name: "Resources");

        migrationBuilder.DropTable(
            name: "UserActions");

        migrationBuilder.DropTable(
            name: "Roles");

        migrationBuilder.DropTable(
            name: "WebhookSubscriptions");

        migrationBuilder.DropTable(
            name: "Users");

        migrationBuilder.DropTable(
            name: "CalibrationAttempts");

        migrationBuilder.DropTable(
            name: "GcodeHarvestOperations");

        migrationBuilder.DropTable(
            name: "MaintenanceTasks");

        migrationBuilder.DropTable(
            name: "PrinterMaintenanceSchedules");

        migrationBuilder.DropTable(
            name: "PartInventories");

        migrationBuilder.DropTable(
            name: "PrintJobs");

        migrationBuilder.DropTable(
            name: "PrintProjects");

        migrationBuilder.DropTable(
            name: "PrinterConfigurationSnapshots");

        migrationBuilder.DropTable(
            name: "MaintenancePlans");

        migrationBuilder.DropTable(
            name: "Toolheads");

        migrationBuilder.DropTable(
            name: "Bins");

        migrationBuilder.DropTable(
            name: "GcodeFiles");

        migrationBuilder.DropTable(
            name: "CalibrationProjects");

        migrationBuilder.DropTable(
            name: "ToolheadModelDefinitions");

        migrationBuilder.DropTable(
            name: "FolderNode");

        migrationBuilder.DropTable(
            name: "FilamentTypes");

        migrationBuilder.DropTable(
            name: "Spools");

        migrationBuilder.DropTable(
            name: "ExtruderModelDefinitions");

        migrationBuilder.DropTable(
            name: "HotendModelDefinitions");

        migrationBuilder.DropTable(
            name: "NozzleModelDefinitions");

        migrationBuilder.DropTable(
            name: "Printers");

        migrationBuilder.DropTable(
            name: "Locations");

        migrationBuilder.DropTable(
            name: "PrinterGroups");

        migrationBuilder.DropTable(
            name: "PrinterModels");

        migrationBuilder.DropTable(
            name: "BedTypes");

        migrationBuilder.DropTable(
            name: "Manufacturers");
    }
}
