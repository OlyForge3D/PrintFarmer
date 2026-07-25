using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AlignDevelopmentAppSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_GcodeFileTag_Tag_TagsId",
            table: "GcodeFileTag");

        migrationBuilder.DropForeignKey(
            name: "FK_Model3DTag_Tag_TagsId",
            table: "Model3DTag");

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterTag_Tag_TagsId",
            table: "PrinterTag");

        migrationBuilder.DropIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId",
            table: "PrintProjectFiles");

        migrationBuilder.DropPrimaryKey(
            name: "PK_Tag",
            table: "Tag");

        migrationBuilder.DropIndex(
            name: "IX_Tag_Name",
            table: "Tag");

        migrationBuilder.RenameTable(
            name: "Tag",
            newName: "Tags");

        migrationBuilder.RenameIndex(
            name: "IX_Tag_CreatedAt",
            table: "Tags",
            newName: "IX_Tags_CreatedAt");

        migrationBuilder.AddColumn<int>(
            name: "PlateIndex",
            table: "PrintProjectFiles",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PlateName",
            table: "PrintProjectFiles",
            type: "TEXT",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "DeadlineAtUtc",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "KwhUsed",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PlateIndex",
            table: "PrintJobs",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PlateName",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "BedTypeId",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuddyCameraIp",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "HasMmu",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastZOffsetCalibrationAt",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "NozzleDiameter",
            table: "Printers",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "UseModelDispatchDefaults",
            table: "Printers",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<decimal>(
            name: "ZOffsetMm",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "DefaultAutoDispatchState",
            table: "PrinterModels",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<Guid>(
            name: "DefaultBedTypeId",
            table: "PrinterModels",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "DefaultStartBehavior",
            table: "PrinterModels",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnJobStarted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EnableTelegramNotifications",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnJobCompleted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnJobFailed",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnJobPaused",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnJobStarted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnJobCompleted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnJobFailed",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnJobPaused",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnJobStarted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnJobCompleted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnJobFailed",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnJobPaused",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnJobStarted",
            table: "NotificationPreferences",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "FilamentPerExtruderColorHex",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentPerExtruderType",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "AppSettingsEntities",
            type: "BLOB",
            nullable: false,
            defaultValue: new byte[0]);

        migrationBuilder.AddColumn<int>(
            name: "Purpose",
            table: "ApiKeys",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "Scopes",
            table: "ApiKeys",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "Category",
            table: "Tags",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "manual");

        migrationBuilder.AddColumn<Guid>(
            name: "ConcurrencyToken",
            table: "Tags",
            type: "TEXT",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.AddColumn<bool>(
            name: "IsAutoGenerated",
            table: "Tags",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "Tags",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddPrimaryKey(
            name: "PK_Tags",
            table: "Tags",
            column: "Id");

        migrationBuilder.CreateTable(
            name: "BarcodeScanLogs",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                Barcode = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                HttpStatus = table.Column<int>(type: "INTEGER", nullable: false),
                MatchedFilamentId = table.Column<int>(type: "INTEGER", nullable: true),
                CreatedSpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BarcodeScanLogs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BedTypes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                Color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BedTypes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CameraSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                CameraId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                EventType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                FilePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true)
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
            name: "CustomFieldDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                EntityType = table.Column<int>(type: "INTEGER", nullable: false),
                FieldName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                FieldKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                FieldType = table.Column<int>(type: "INTEGER", nullable: false),
                Options = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                IsRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                DefaultValue = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomFieldDefinitions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "LibrarySyncChanges",
            columns: table => new
            {
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                Operation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Visibility = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ActorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibrarySyncChanges", x => x.Revision);
            });

        migrationBuilder.CreateTable(
            name: "LoginAuditEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                Username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Success = table.Column<bool>(type: "INTEGER", nullable: false),
                IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                FailureReason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LoginAuditEntries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaterialClusters",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaterialClusters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ModelCollections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                IsShared = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ModelCollections", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "NfcTagBindings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TagUid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                SpoolName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                TrayId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                SpoolLastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                ProviderType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DeviceAddress = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ElectricityRateUsdPerKwh = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
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
            name: "PrinterGroupAccesses",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                AccessLevel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                CreatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
            name: "PrintJobTag",
            columns: table => new
            {
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagsId = table.Column<Guid>(type: "TEXT", nullable: false)
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
            name: "PrintQuotas",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                GroupName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                QuotaType = table.Column<int>(type: "INTEGER", nullable: false),
                LimitAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                UsedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                PeriodType = table.Column<int>(type: "INTEGER", nullable: false),
                PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                ResetAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                P256dh = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Auth = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
            name: "UserBalances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                BalanceAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false, defaultValue: "USD"),
                LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                CredentialId = table.Column<byte[]>(type: "BLOB", nullable: false),
                PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                SignCount = table.Column<uint>(type: "INTEGER", nullable: false),
                DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                AaguidDescription = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                GroupName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "UserSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Theme = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Locale = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                ItemsPerPage = table.Column<int>(type: "INTEGER", nullable: false),
                DefaultSlicerPreset = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                PrintablesUsername = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                PrintablesOAuthAccessToken = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                PrintablesOAuthRefreshToken = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                PrintablesOAuthTokenType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                PrintablesOAuthScope = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                PrintablesOAuthTokenExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                PrintablesOAuthLinkedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false)
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
            name: "CustomFieldValues",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
            name: "MaterialClusterMembers",
            columns: table => new
            {
                ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                FilamentTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
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
            name: "PowerReadings",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                PowerMonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                WattsNow = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: false),
                KwhTotal = table.Column<decimal>(type: "TEXT", precision: 14, scale: 4, nullable: true),
                RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "BalanceTransactions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserBalanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                TransactionType = table.Column<int>(type: "INTEGER", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                PerformedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId_PlateIndex",
            table: "PrintProjectFiles",
            columns: new[] { "PrintProjectId", "GcodeFileId", "PlateIndex" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_DeadlineAtUtc",
            table: "PrintJobs",
            column: "DeadlineAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Printers_BedTypeId",
            table: "Printers",
            column: "BedTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModels_DefaultBedTypeId",
            table: "PrinterModels",
            column: "DefaultBedTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_Tags_Category_IsAutoGenerated",
            table: "Tags",
            columns: new[] { "Category", "IsAutoGenerated" });

        migrationBuilder.CreateIndex(
            name: "IX_Tags_Name_Category",
            table: "Tags",
            columns: new[] { "Name", "Category" },
            unique: true);

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
            name: "IX_BarcodeScanLogs_Outcome",
            table: "BarcodeScanLogs",
            column: "Outcome");

        migrationBuilder.CreateIndex(
            name: "IX_BarcodeScanLogs_Timestamp",
            table: "BarcodeScanLogs",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_BedTypes_Name",
            table: "BedTypes",
            column: "Name",
            unique: true);

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
            name: "IX_MaterialClusterMembers_FilamentTypeId",
            table: "MaterialClusterMembers",
            column: "FilamentTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_MaterialClusters_Name",
            table: "MaterialClusters",
            column: "Name",
            unique: true);

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
            name: "IX_PrinterGroupAccesses_PrinterGroupId_RoleId_AccessLevel",
            table: "PrinterGroupAccesses",
            columns: new[] { "PrinterGroupId", "RoleId", "AccessLevel" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterGroupAccesses_RoleId",
            table: "PrinterGroupAccesses",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobTag_TagsId",
            table: "PrintJobTag",
            column: "TagsId");

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
            name: "IX_UserSettings_UserId",
            table: "UserSettings",
            column: "UserId",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_GcodeFileTag_Tags_TagsId",
            table: "GcodeFileTag",
            column: "TagsId",
            principalTable: "Tags",
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
            name: "FK_PrinterModels_BedTypes_DefaultBedTypeId",
            table: "PrinterModels",
            column: "DefaultBedTypeId",
            principalTable: "BedTypes",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_Printers_BedTypes_BedTypeId",
            table: "Printers",
            column: "BedTypeId",
            principalTable: "BedTypes",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterTag_Tags_TagsId",
            table: "PrinterTag",
            column: "TagsId",
            principalTable: "Tags",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_GcodeFileTag_Tags_TagsId",
            table: "GcodeFileTag");

        migrationBuilder.DropForeignKey(
            name: "FK_Model3DTag_Tags_TagsId",
            table: "Model3DTag");

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterModels_BedTypes_DefaultBedTypeId",
            table: "PrinterModels");

        migrationBuilder.DropForeignKey(
            name: "FK_Printers_BedTypes_BedTypeId",
            table: "Printers");

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterTag_Tags_TagsId",
            table: "PrinterTag");

        migrationBuilder.DropTable(
            name: "BalanceTransactions");

        migrationBuilder.DropTable(
            name: "BarcodeScanLogs");

        migrationBuilder.DropTable(
            name: "BedTypes");

        migrationBuilder.DropTable(
            name: "CameraSnapshots");

        migrationBuilder.DropTable(
            name: "CustomFieldValues");

        migrationBuilder.DropTable(
            name: "LibrarySyncChanges");

        migrationBuilder.DropTable(
            name: "LoginAuditEntries");

        migrationBuilder.DropTable(
            name: "MaterialClusterMembers");

        migrationBuilder.DropTable(
            name: "ModelCollectionMemberships");

        migrationBuilder.DropTable(
            name: "NfcTagBindings");

        migrationBuilder.DropTable(
            name: "PowerReadings");

        migrationBuilder.DropTable(
            name: "PrinterGroupAccesses");

        migrationBuilder.DropTable(
            name: "PrintJobTag");

        migrationBuilder.DropTable(
            name: "PrintQuotas");

        migrationBuilder.DropTable(
            name: "PushSubscriptions");

        migrationBuilder.DropTable(
            name: "UserPasskeyCredentials");

        migrationBuilder.DropTable(
            name: "UserQuotaGroupMemberships");

        migrationBuilder.DropTable(
            name: "UserSettings");

        migrationBuilder.DropTable(
            name: "UserBalances");

        migrationBuilder.DropTable(
            name: "CustomFieldDefinitions");

        migrationBuilder.DropTable(
            name: "MaterialClusters");

        migrationBuilder.DropTable(
            name: "ModelCollections");

        migrationBuilder.DropTable(
            name: "PowerMonitors");

        migrationBuilder.DropIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId_PlateIndex",
            table: "PrintProjectFiles");

        migrationBuilder.DropIndex(
            name: "IX_PrintJobs_DeadlineAtUtc",
            table: "PrintJobs");

        migrationBuilder.DropIndex(
            name: "IX_Printers_BedTypeId",
            table: "Printers");

        migrationBuilder.DropIndex(
            name: "IX_PrinterModels_DefaultBedTypeId",
            table: "PrinterModels");

        migrationBuilder.DropPrimaryKey(
            name: "PK_Tags",
            table: "Tags");

        migrationBuilder.DropIndex(
            name: "IX_Tags_Category_IsAutoGenerated",
            table: "Tags");

        migrationBuilder.DropIndex(
            name: "IX_Tags_Name_Category",
            table: "Tags");

        migrationBuilder.DropColumn(
            name: "PlateIndex",
            table: "PrintProjectFiles");

        migrationBuilder.DropColumn(
            name: "PlateName",
            table: "PrintProjectFiles");

        migrationBuilder.DropColumn(
            name: "DeadlineAtUtc",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "KwhUsed",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PlateIndex",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PlateName",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "BedTypeId",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "BuddyCameraIp",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "HasMmu",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "LastZOffsetCalibrationAt",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "NozzleDiameter",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "UseModelDispatchDefaults",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "ZOffsetMm",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "DefaultAutoDispatchState",
            table: "PrinterModels");

        migrationBuilder.DropColumn(
            name: "DefaultBedTypeId",
            table: "PrinterModels");

        migrationBuilder.DropColumn(
            name: "DefaultStartBehavior",
            table: "PrinterModels");

        migrationBuilder.DropColumn(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnJobStarted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EnableTelegramNotifications",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnJobCompleted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnJobFailed",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnJobPaused",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnJobStarted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnJobCompleted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnJobFailed",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnJobPaused",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnJobStarted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnJobCompleted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnJobFailed",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnJobPaused",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnJobStarted",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "FilamentPerExtruderColorHex",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "FilamentPerExtruderType",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "AppSettingsEntities");

        migrationBuilder.DropColumn(
            name: "Purpose",
            table: "ApiKeys");

        migrationBuilder.DropColumn(
            name: "Scopes",
            table: "ApiKeys");

        migrationBuilder.DropColumn(
            name: "Category",
            table: "Tags");

        migrationBuilder.DropColumn(
            name: "ConcurrencyToken",
            table: "Tags");

        migrationBuilder.DropColumn(
            name: "IsAutoGenerated",
            table: "Tags");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "Tags");

        migrationBuilder.RenameTable(
            name: "Tags",
            newName: "Tag");

        migrationBuilder.RenameIndex(
            name: "IX_Tags_CreatedAt",
            table: "Tag",
            newName: "IX_Tag_CreatedAt");

        migrationBuilder.AddPrimaryKey(
            name: "PK_Tag",
            table: "Tag",
            column: "Id");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId",
            table: "PrintProjectFiles",
            columns: new[] { "PrintProjectId", "GcodeFileId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Tag_Name",
            table: "Tag",
            column: "Name",
            unique: true);

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

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterTag_Tag_TagsId",
            table: "PrinterTag",
            column: "TagsId",
            principalTable: "Tag",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
