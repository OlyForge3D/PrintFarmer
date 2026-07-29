using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class ReconcileEpic705AppSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles");

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions");

        migrationBuilder.DropIndex(
            name: "IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropIndex(
            name: "IX_MaintenanceLogs_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.AddColumn<DateTime>(
            name: "AnchorAtUtc",
            table: "UserTasks",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AnchorKind",
            table: "UserTasks",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.AddColumn<long>(
            name: "LastMutationSequence",
            table: "UserTasks",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "SourceId",
            table: "UserTasks",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceKind",
            table: "UserTasks",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.AddColumn<DateTime>(
            name: "WindowEndUtc",
            table: "UserTasks",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "WindowStartUtc",
            table: "UserTasks",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "CumulativePrintHours",
            table: "Toolheads",
            type: "REAL",
            nullable: false,
            defaultValue: 0.0);

        migrationBuilder.AddColumn<bool>(
            name: "IsFilamentUsageAuthoritative",
            table: "PrintJobToolheadUsages",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "SpoolSourceIdentity",
            table: "PrintJobToolheadUsages",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SpoolSourceKind",
            table: "PrintJobToolheadUsages",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "HarvestOperationKey",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "HarvestedAt",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "HarvestedByUserId",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 450,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "HarvestedIntoBinId",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RequiredMaterialsPerToolJson",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ExternalBaselineInitializedUtc",
            table: "PrinterStatisticsSet",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ExternalJobsCompleted",
            table: "PrinterStatisticsSet",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<double>(
            name: "ExternalPrintHours",
            table: "PrinterStatisticsSet",
            type: "REAL",
            nullable: false,
            defaultValue: 0.0);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastExternalHoursAttributionUtc",
            table: "PrinterStatisticsSet",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "SupportsPerToolAttribution",
            table: "Printers",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<Guid>(
            name: "ToolheadId",
            table: "PrinterMaintenanceSchedules",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "INTEGER",
            oldDefaultValue: true);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "INTEGER",
            oldDefaultValue: true);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "INTEGER",
            oldDefaultValue: true);

        migrationBuilder.AddColumn<string>(
            name: "AttentionPushCategoryPreferencesJson",
            table: "NotificationPreferences",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnFilamentRunout",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnHarvestReady",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnPrinterFailure",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnPrinterOffline",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnFilamentRunout",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnHarvestReady",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnPrinterFailure",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnPrinterOffline",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnFilamentRunout",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnHarvestReady",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnPrinterFailure",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnPrinterOffline",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnFilamentRunout",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnHarvestReady",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnPrinterFailure",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnPrinterOffline",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<double>(
            name: "ToolheadHoursAtMaintenance",
            table: "MaintenanceLogs",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ToolheadId",
            table: "MaintenanceLogs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ToolheadId",
            table: "MaintenanceAlerts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ResolvedAtUtc",
            table: "FailureDetectionIncidents",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "BinId",
            table: "BarcodeScanLogs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PartInventoryId",
            table: "BarcodeScanLogs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AttentionSnoozes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                AttentionItemId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                SnoozedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                AttentionItemAnchorAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AttentionSnoozes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Bins",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Bins", x => x.Id);
                table.CheckConstraint("CK_Bins_Code_Normalized", "\"Code\" = UPPER(\"Code\")");
            });

        migrationBuilder.CreateTable(
            name: "DeviceTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RegistrationVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                InstallationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Token = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Platform = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Environment = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                AppBundleId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastFailureAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ConsecutiveFailureCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
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
            name: "FilamentFallbackGroups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                NameNormalized = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                MaterialType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "FilamentSwapOverrides",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToolheadIndex = table.Column<int>(type: "INTEGER", nullable: false),
                SpoolId = table.Column<int>(type: "INTEGER", nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                ExpectedMaterial = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                ScannedMaterial = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                AffectedJobIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentSwapOverrides", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "IdempotencyRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RouteKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                ResponseContentType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                ResponseBody = table.Column<byte[]>(type: "BLOB", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MutationCounters",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Value = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MutationCounters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PartInventories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                ModelFileRef = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                DefaultBinId = table.Column<Guid>(type: "TEXT", nullable: true),
                OnHand = table.Column<int>(type: "INTEGER", nullable: false),
                ReorderPoint = table.Column<int>(type: "INTEGER", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "FilamentFallbackGroupMembers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                FallbackGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToolheadId = table.Column<Guid>(type: "TEXT", nullable: false),
                Position = table.Column<int>(type: "INTEGER", nullable: false)
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
            name: "PartInventoryAdjustments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                BinId = table.Column<Guid>(type: "TEXT", nullable: true),
                Delta = table.Column<int>(type: "INTEGER", nullable: false),
                ResultingBalance = table.Column<int>(type: "INTEGER", nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                OperationKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "PartOutputMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                PrintProjectFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "PrintJobPartOutputSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                Sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                QuantityPerPrint = table.Column<int>(type: "INTEGER", nullable: false),
                ExpectedBinId = table.Column<Guid>(type: "TEXT", nullable: true),
                ExpectedBinCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                SourceKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                SourceFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceMappingId = table.Column<Guid>(type: "TEXT", nullable: false),
                Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "PartHarvestOutputSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                PartInventoryAdjustmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                JobOutputSnapshotId = table.Column<Guid>(type: "TEXT", nullable: true),
                Sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                ExpectedBinId = table.Column<Guid>(type: "TEXT", nullable: true),
                ExpectedBinCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                ActualBinId = table.Column<Guid>(type: "TEXT", nullable: false),
                ActualBinCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Origin = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                SourceFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceMappingId = table.Column<Guid>(type: "TEXT", nullable: true),
                OverrideApplied = table.Column<bool>(type: "INTEGER", nullable: false),
                OverrideReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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

        migrationBuilder.InsertData(
            table: "MutationCounters",
            columns: new[] { "Id", "Value" },
            values: new object[] { 1, 0L });

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_OpenProfileImport",
            table: "UserTasks",
            columns: new[] { "TaskType", "EntityType", "EntityId" },
            unique: true,
            filter: "\"TaskType\" = 1 AND \"EntityType\" = 'PrinterModel' AND \"Status\" IN (0, 1)");

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_SourceKind_SourceId",
            table: "UserTasks",
            columns: new[] { "SourceKind", "SourceId" },
            unique: true,
            filter: "\"SourceId\" IS NOT NULL AND \"Status\" IN (0, 1)");

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
            name: "UX_Toolheads_PrinterId_Index",
            table: "Toolheads",
            columns: new[] { "PrinterId", "Index" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobToolheadUsages_SpoolProjection",
            table: "PrintJobToolheadUsages",
            columns: new[] { "SpoolSourceKind", "SpoolSourceIdentity", "SpoolmanSpoolId", "IsFilamentUsageAuthoritative" });

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
            unique: true);

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
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            unique: true,
            filter: "\"ResolvedAlertId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_ToolheadId",
            table: "MaintenanceLogs",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceAlerts_ToolheadId",
            table: "MaintenanceAlerts",
            column: "ToolheadId");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_BinId",
            table: "BarcodeScanLogs",
            column: "BinId");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_PartInventoryId",
            table: "BarcodeScanLogs",
            column: "PartInventoryId");

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
            name: "IX_Bins_Code",
            table: "Bins",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Bins_IsActive",
            table: "Bins",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_Token",
            table: "DeviceTokens",
            column: "Token");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_UserId_InstallationId",
            table: "DeviceTokens",
            columns: new[] { "UserId", "InstallationId" },
            unique: true);

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
            name: "IX_IdempotencyRecords_CreatedAt",
            table: "IdempotencyRecords",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords",
            columns: new[] { "UserId", "RouteKey", "IdempotencyKey" },
            unique: true);

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
            unique: true);

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
            unique: true);

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
            unique: true);

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

        migrationBuilder.AddForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles",
            column: "FolderId",
            principalTable: "FolderNode",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceAlerts_Toolheads_ToolheadId",
            table: "MaintenanceAlerts",
            column: "ToolheadId",
            principalTable: "Toolheads",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            principalTable: "MaintenanceAlerts",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_Toolheads_ToolheadId",
            table: "MaintenanceLogs",
            column: "ToolheadId",
            principalTable: "Toolheads",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Toolheads_ToolheadId",
            table: "PrinterMaintenanceSchedules",
            column: "ToolheadId",
            principalTable: "Toolheads",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_PrintJobs_Bins_HarvestedIntoBinId",
            table: "PrintJobs",
            column: "HarvestedIntoBinId",
            principalTable: "Bins",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions",
            column: "ManufacturerId",
            principalTable: "Manufacturers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles");

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceAlerts_Toolheads_ToolheadId",
            table: "MaintenanceAlerts");

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_Toolheads_ToolheadId",
            table: "MaintenanceLogs");

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Toolheads_ToolheadId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropForeignKey(
            name: "FK_PrintJobs_Bins_HarvestedIntoBinId",
            table: "PrintJobs");

        migrationBuilder.DropForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions");

        migrationBuilder.DropTable(
            name: "AttentionSnoozes");

        migrationBuilder.DropTable(
            name: "DeviceTokens");

        migrationBuilder.DropTable(
            name: "FilamentFallbackGroupMembers");

        migrationBuilder.DropTable(
            name: "FilamentSwapOverrides");

        migrationBuilder.DropTable(
            name: "IdempotencyRecords");

        migrationBuilder.DropTable(
            name: "MutationCounters");

        migrationBuilder.DropTable(
            name: "PartHarvestOutputSnapshots");

        migrationBuilder.DropTable(
            name: "PartOutputMappings");

        migrationBuilder.DropTable(
            name: "PrintJobPartOutputSnapshots");

        migrationBuilder.DropTable(
            name: "FilamentFallbackGroups");

        migrationBuilder.DropTable(
            name: "PartInventoryAdjustments");

        migrationBuilder.DropTable(
            name: "PartInventories");

        migrationBuilder.DropTable(
            name: "Bins");

        migrationBuilder.DropIndex(
            name: "IX_UserTasks_OpenProfileImport",
            table: "UserTasks");

        migrationBuilder.DropIndex(
            name: "IX_UserTasks_SourceKind_SourceId",
            table: "UserTasks");

        migrationBuilder.DropIndex(
            name: "IX_UserTasks_Status_AnchorKind_AnchorAtUtc",
            table: "UserTasks");

        migrationBuilder.DropIndex(
            name: "IX_UserTasks_Status_SourceKind_SourceId",
            table: "UserTasks");

        migrationBuilder.DropIndex(
            name: "IX_UserTasks_Status_UpdatedAt",
            table: "UserTasks");

        migrationBuilder.DropIndex(
            name: "UX_Toolheads_PrinterId_Index",
            table: "Toolheads");

        migrationBuilder.DropIndex(
            name: "IX_PrintJobToolheadUsages_SpoolProjection",
            table: "PrintJobToolheadUsages");

        migrationBuilder.DropIndex(
            name: "IX_PrintJobs_HarvestedAt",
            table: "PrintJobs");

        migrationBuilder.DropIndex(
            name: "IX_PrintJobs_HarvestedIntoBinId",
            table: "PrintJobs");

        migrationBuilder.DropIndex(
            name: "IX_PrintJobs_HarvestOperationKey",
            table: "PrintJobs");

        migrationBuilder.DropIndex(
            name: "IX_PrinterMaintenanceSchedules_ToolheadId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_NullToolhead",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropIndex(
            name: "UX_PrinterMaintenanceSchedules_Plan_Printer_Toolhead",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropIndex(
            name: "IX_MaintenanceLogs_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.DropIndex(
            name: "IX_MaintenanceLogs_ToolheadId",
            table: "MaintenanceLogs");

        migrationBuilder.DropIndex(
            name: "IX_MaintenanceAlerts_ToolheadId",
            table: "MaintenanceAlerts");

        migrationBuilder.DropIndex(
            name: "IX_BarcodeScanLogs_BinId",
            table: "BarcodeScanLogs");

        migrationBuilder.DropIndex(
            name: "IX_BarcodeScanLogs_PartInventoryId",
            table: "BarcodeScanLogs");

        migrationBuilder.DropColumn(
            name: "AnchorAtUtc",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "AnchorKind",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "LastMutationSequence",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "SourceId",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "SourceKind",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "WindowEndUtc",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "WindowStartUtc",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "CumulativePrintHours",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "IsFilamentUsageAuthoritative",
            table: "PrintJobToolheadUsages");

        migrationBuilder.DropColumn(
            name: "SpoolSourceIdentity",
            table: "PrintJobToolheadUsages");

        migrationBuilder.DropColumn(
            name: "SpoolSourceKind",
            table: "PrintJobToolheadUsages");

        migrationBuilder.DropColumn(
            name: "HarvestOperationKey",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "HarvestedAt",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "HarvestedByUserId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "HarvestedIntoBinId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "RequiredMaterialsPerToolJson",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "ExternalBaselineInitializedUtc",
            table: "PrinterStatisticsSet");

        migrationBuilder.DropColumn(
            name: "ExternalJobsCompleted",
            table: "PrinterStatisticsSet");

        migrationBuilder.DropColumn(
            name: "ExternalPrintHours",
            table: "PrinterStatisticsSet");

        migrationBuilder.DropColumn(
            name: "LastExternalHoursAttributionUtc",
            table: "PrinterStatisticsSet");

        migrationBuilder.DropColumn(
            name: "SupportsPerToolAttribution",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "ToolheadId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.DropColumn(
            name: "AttentionPushCategoryPreferencesJson",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnPrinterOffline",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnPrinterOffline",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnPrinterOffline",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnPrinterOffline",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "ToolheadHoursAtMaintenance",
            table: "MaintenanceLogs");

        migrationBuilder.DropColumn(
            name: "ToolheadId",
            table: "MaintenanceLogs");

        migrationBuilder.DropColumn(
            name: "ToolheadId",
            table: "MaintenanceAlerts");

        migrationBuilder.DropColumn(
            name: "ResolvedAtUtc",
            table: "FailureDetectionIncidents");

        migrationBuilder.DropColumn(
            name: "BinId",
            table: "BarcodeScanLogs");

        migrationBuilder.DropColumn(
            name: "PartInventoryId",
            table: "BarcodeScanLogs");

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "INTEGER",
            oldDefaultValue: false);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "INTEGER",
            oldDefaultValue: false);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "INTEGER",
            oldDefaultValue: false);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId",
            table: "PrinterMaintenanceSchedules",
            columns: new[] { "MaintenancePlanId", "PrinterId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceLogs_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId");

        migrationBuilder.AddForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles",
            column: "FolderId",
            principalTable: "FolderNode",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            principalTable: "MaintenanceAlerts",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions",
            column: "ManufacturerId",
            principalTable: "Manufacturers",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }
}
