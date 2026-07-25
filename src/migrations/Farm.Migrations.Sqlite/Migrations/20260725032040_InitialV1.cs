using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class InitialV1 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ApiKeys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ApiKeys", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AppSettingsEntities",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AppSettingsEntities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogVersions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Version = table.Column<string>(type: "TEXT", nullable: false),
                ManifestHash = table.Column<string>(type: "TEXT", nullable: true),
                AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogVersions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "DispatchSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AutoDispatchEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AutoDispatchMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                IdleThresholdSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                MinimumScoreThreshold = table.Column<double>(type: "REAL", nullable: false),
                MaxConcurrentDispatches = table.Column<int>(type: "INTEGER", nullable: false),
                LoadBalancingStrategy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DispatchSettings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FailedLoginAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Identifier = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                FailureReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FailedLoginAttempts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FilamentTypes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DefaultHotendTemp = table.Column<double>(type: "REAL", nullable: true),
                DefaultBedTemp = table.Column<double>(type: "REAL", nullable: true),
                IsAbrasive = table.Column<bool>(type: "INTEGER", nullable: false),
                NeedsEnclosure = table.Column<bool>(type: "INTEGER", nullable: false),
                DefaultPricePerKg = table.Column<double>(type: "REAL", nullable: true),
                DefaultDensity = table.Column<double>(type: "REAL", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentTypes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FileHealthAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                AuditDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                AuditType = table.Column<int>(type: "INTEGER", nullable: false),
                FilesChecked = table.Column<int>(type: "INTEGER", nullable: false),
                HealthyFiles = table.Column<int>(type: "INTEGER", nullable: false),
                MissingFiles = table.Column<int>(type: "INTEGER", nullable: false),
                CorruptedFiles = table.Column<int>(type: "INTEGER", nullable: false),
                OrphanedFiles = table.Column<int>(type: "INTEGER", nullable: false),
                MissingFileIds = table.Column<string>(type: "TEXT", nullable: true),
                CorruptedFileIds = table.Column<string>(type: "TEXT", nullable: true),
                OrphanedFilePaths = table.Column<string>(type: "TEXT", nullable: true),
                SummaryMessage = table.Column<string>(type: "TEXT", nullable: true),
                HasIssues = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileHealthAudits", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FolderNode",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                FolderType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FolderNode", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Locations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                PrinterCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                Path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false, defaultValue: "/"),
                Depth = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                TotalPrinterCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
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
            name: "MaintenanceComponents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Sku = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                UnitCost = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                Supplier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                InStock = table.Column<int>(type: "INTEGER", nullable: false),
                MinimumStock = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceComponents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TaskName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                IntervalHours = table.Column<double>(type: "REAL", nullable: true),
                IntervalDays = table.Column<int>(type: "INTEGER", nullable: true),
                EstimatedDurationMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                Priority = table.Column<int>(type: "INTEGER", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                RequiresEnclosure = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresCarbonFilter = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresHepaFilter = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresBowdenTube = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresPtfeLiner = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresLinearRails = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresLeadScrews = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresToolchanger = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresFilamentCutter = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresHeatedChamber = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresHeatedBed = table.Column<bool>(type: "INTEGER", nullable: true),
                RequiresMultiMaterial = table.Column<bool>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceTasks", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Manufacturers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Url = table.Column<string>(type: "TEXT", nullable: true),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                NameLowered = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Manufacturers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ObicoServers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                ApiKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                MaxConcurrentAnalyses = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ObicoServers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PasswordPolicies",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                MinLength = table.Column<int>(type: "INTEGER", nullable: false),
                RequireUppercase = table.Column<bool>(type: "INTEGER", nullable: false),
                RequireLowercase = table.Column<bool>(type: "INTEGER", nullable: false),
                RequireDigit = table.Column<bool>(type: "INTEGER", nullable: false),
                RequireSymbol = table.Column<bool>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PasswordPolicies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrinterGroups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterGroups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                DueDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjectTemplates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                DefaultPriority = table.Column<int>(type: "INTEGER", nullable: false),
                DefaultNotes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                IsSystemTemplate = table.Column<bool>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjectTemplates", x => x.Id);
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
            name: "RetryPolicies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                MaxRetries = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 3),
                InitialDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 60),
                ExponentialBase = table.Column<double>(type: "REAL", nullable: false, defaultValue: 2.0),
                MaxDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 3600),
                RetryOnErrorCategories = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "Recoverable"),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RetryPolicies", x => x.Id);
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
            name: "SystemLogs",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                Level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                Exception = table.Column<string>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Metadata = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SystemLogs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Tag",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tag", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UserActions",
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
                table.PrimaryKey("PK_UserActions", x => x.Id);
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
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                FailedLoginAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                LockoutEnd = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastFailedLogin = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WebhookSubscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                Secret = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                EventTypes = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                MaxConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 10),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastDeliveryAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceTaskComponents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                MaintenanceTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                MaintenanceComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                GearRatio = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                IsDirectDrive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                MaxTemp = table.Column<int>(type: "INTEGER", nullable: true, defaultValue: 300),
                IsHighFlow = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                MaxFlowRate = table.Column<double>(type: "REAL", nullable: true),
                NozzleInterface = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Diameter = table.Column<double>(type: "REAL", nullable: false),
                MaxTemp = table.Column<int>(type: "INTEGER", nullable: true, defaultValue: 500),
                NozzleType = table.Column<int>(type: "INTEGER", nullable: false),
                NozzleInterface = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                MotionType = table.Column<int>(type: "INTEGER", nullable: true),
                MaxX = table.Column<double>(type: "REAL", nullable: true),
                MaxY = table.Column<double>(type: "REAL", nullable: true),
                MaxZ = table.Column<double>(type: "REAL", nullable: true),
                DefaultBackend = table.Column<int>(type: "INTEGER", nullable: true),
                HasHeatedBed = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                HasEnclosure = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasCarbonFilter = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasHepaFilter = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasBowdenTube = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasPtfeLiner = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasLinearRails = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasLeadScrews = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasToolchanger = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasFilamentCutter = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                HasHeatedChamber = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                MultiMaterial = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                SupportsAutoLeveling = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                MaxBedTemp = table.Column<int>(type: "INTEGER", nullable: true, defaultValue: 120),
                MaxPrintSpeed = table.Column<int>(type: "INTEGER", nullable: true, defaultValue: 150),
                CoverImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                BedTextureUrl = table.Column<string>(type: "TEXT", nullable: true),
                DefaultWattage = table.Column<decimal>(type: "TEXT", nullable: true),
                DefaultHourlyRate = table.Column<decimal>(type: "TEXT", nullable: true),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                NameLowered = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterModels", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterModels_Manufacturers_ManufacturerId",
                    column: x => x.ManufacturerId,
                    principalTable: "Manufacturers",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "PrintProjectTemplateFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintProjectTemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                FileNamePattern = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                ColorRequirement = table.Column<int>(type: "INTEGER", nullable: false),
                MaterialRequirement = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                PrintCount = table.Column<int>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
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
            name: "Model3DTag",
            columns: table => new
            {
                Model3DId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagsId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Model3DTag", x => new { x.Model3DId, x.TagsId });
                table.ForeignKey(
                    name: "FK_Model3DTag_Tag_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tag",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                EventType = table.Column<int>(type: "INTEGER", nullable: false),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Success = table.Column<bool>(type: "INTEGER", nullable: false),
                FailureReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Metadata = table.Column<string>(type: "TEXT", nullable: true),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
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
            name: "NotificationPreferences",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                EnableEmailNotifications = table.Column<bool>(type: "INTEGER", nullable: false),
                EnablePushNotifications = table.Column<bool>(type: "INTEGER", nullable: false),
                EnableInAppNotifications = table.Column<bool>(type: "INTEGER", nullable: false),
                NotifyOnCompletion = table.Column<bool>(type: "INTEGER", nullable: false),
                NotifyOnFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                NotifyOnStart = table.Column<bool>(type: "INTEGER", nullable: false),
                NotifyOnPause = table.Column<bool>(type: "INTEGER", nullable: false),
                Frequency = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                RetentionDays = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Token = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsUsed = table.Column<bool>(type: "INTEGER", nullable: false),
                UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                UsedByIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true)
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
            name: "RefreshTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Token = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                RevokedByIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                ReplacedByToken = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                CreatedByIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                RevokedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true)
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
            name: "UserTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TaskType = table.Column<int>(type: "INTEGER", nullable: false),
                EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Priority = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DueAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                DismissedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                DismissedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                RelatedEntityIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                WebhookSubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                Success = table.Column<bool>(type: "INTEGER", nullable: false),
                ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DefaultHotendId = table.Column<Guid>(type: "TEXT", nullable: true),
                DefaultExtruderId = table.Column<Guid>(type: "TEXT", nullable: true),
                DefaultNozzleId = table.Column<Guid>(type: "TEXT", nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "TEXT", nullable: true)
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
                    onDelete: ReferentialAction.SetNull);
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
                PrinterModelsId = table.Column<Guid>(type: "TEXT", nullable: false),
                SupportedFilamentTypesId = table.Column<Guid>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                SlicerModelName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SlicerType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ServerUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                OriginalServerUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                BackendPort = table.Column<int>(type: "INTEGER", nullable: false),
                FrontendPort = table.Column<int>(type: "INTEGER", nullable: true),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                Backend = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                Username = table.Column<string>(type: "TEXT", nullable: true),
                Password = table.Column<string>(type: "TEXT", nullable: true),
                ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                TemplateMachineProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                LocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                PrinterGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                DateAcquired = table.Column<DateTime>(type: "TEXT", nullable: true),
                MaxBuildVolumeX = table.Column<double>(type: "REAL", nullable: true),
                MaxBuildVolumeY = table.Column<double>(type: "REAL", nullable: true),
                MaxBuildVolumeZ = table.Column<double>(type: "REAL", nullable: true),
                HasHeatedBed = table.Column<bool>(type: "INTEGER", nullable: false),
                HasEnclosure = table.Column<bool>(type: "INTEGER", nullable: false),
                MultiMaterial = table.Column<bool>(type: "INTEGER", nullable: false),
                SupportsAutoLeveling = table.Column<bool>(type: "INTEGER", nullable: false),
                MaxPrintSpeed = table.Column<int>(type: "INTEGER", nullable: true),
                MaxBedTemp = table.Column<int>(type: "INTEGER", nullable: true),
                CurrentMaterial = table.Column<string>(type: "TEXT", nullable: true),
                CurrentSpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                Wattage = table.Column<decimal>(type: "TEXT", nullable: true),
                MachineHourlyRate = table.Column<decimal>(type: "TEXT", nullable: true),
                ObicoEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                InMaintenance = table.Column<bool>(type: "INTEGER", nullable: false),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AutoDispatchEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Printers", x => x.Id);
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
            name: "PrinterModelToolheads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Index = table.Column<int>(type: "INTEGER", nullable: false),
                HotendModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                ExtruderModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                ToolheadModelDefId = table.Column<Guid>(type: "TEXT", nullable: true),
                NozzleModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                SupportedMaterials = table.Column<string>(type: "TEXT", nullable: true),
                IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                StreamUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                SnapshotUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                Location = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CameraType = table.Column<string>(type: "TEXT", nullable: false),
                HealthStatus = table.Column<string>(type: "TEXT", nullable: false),
                LastHealthCheck = table.Column<DateTime>(type: "TEXT", nullable: true),
                HealthMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                JobName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                Confidence = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                SnapshotUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                AutoPaused = table.Column<bool>(type: "INTEGER", nullable: false)
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
            name: "GcodeFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Source = table.Column<int>(type: "INTEGER", nullable: false),
                SourcePrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                OriginalPrinterPath = table.Column<string>(type: "TEXT", nullable: true),
                LastSeenOnPrinter = table.Column<DateTime>(type: "TEXT", nullable: true),
                RequiredNozzleDiameter = table.Column<double>(type: "REAL", nullable: true),
                RequiredMaterial = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                EstimatedPrintTimeMinutes = table.Column<double>(type: "REAL", nullable: true),
                EstimatedFilamentLengthMm = table.Column<double>(type: "REAL", nullable: true),
                EstimatedFilamentWeightG = table.Column<double>(type: "REAL", nullable: true),
                ExtractedPrinterModelName = table.Column<string>(type: "TEXT", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                SlicerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                SlicerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                PrintSettingsId = table.Column<string>(type: "TEXT", nullable: true),
                LayerHeight = table.Column<double>(type: "REAL", nullable: true),
                InfillPercentage = table.Column<double>(type: "REAL", nullable: true),
                Perimeters = table.Column<int>(type: "INTEGER", nullable: true),
                PrintTemperature = table.Column<double>(type: "REAL", nullable: true),
                BedTemperature = table.Column<double>(type: "REAL", nullable: true),
                PrintSpeed = table.Column<double>(type: "REAL", nullable: true),
                TotalLayers = table.Column<int>(type: "INTEGER", nullable: true),
                FirstLayerHeight = table.Column<double>(type: "REAL", nullable: true),
                SupportEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                ToolChangesCount = table.Column<int>(type: "INTEGER", nullable: true),
                ObjectDimensionX = table.Column<double>(type: "REAL", nullable: true),
                ObjectDimensionY = table.Column<double>(type: "REAL", nullable: true),
                ObjectDimensionZ = table.Column<double>(type: "REAL", nullable: true),
                ObjectCount = table.Column<int>(type: "INTEGER", nullable: true),
                RetractionLength = table.Column<double>(type: "REAL", nullable: true),
                RetractionSpeed = table.Column<double>(type: "REAL", nullable: true),
                TopSolidLayers = table.Column<int>(type: "INTEGER", nullable: true),
                BottomSolidLayers = table.Column<int>(type: "INTEGER", nullable: true),
                MaxVolumetricSpeed = table.Column<double>(type: "REAL", nullable: true),
                IroningEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                PrinterGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                FilamentPerExtruderWeightG = table.Column<string>(type: "TEXT", nullable: true),
                FilamentPerExtruderLengthMm = table.Column<string>(type: "TEXT", nullable: true),
                ExtruderCount = table.Column<int>(type: "INTEGER", nullable: true),
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
                table.PrimaryKey("PK_GcodeFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_GcodeFiles_FolderNode_FolderId",
                    column: x => x.FolderId,
                    principalTable: "FolderNode",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                ErrorType = table.Column<string>(type: "TEXT", nullable: true),
                ErrorPhase = table.Column<string>(type: "TEXT", nullable: true),
                ErrorDetails = table.Column<string>(type: "TEXT", nullable: true),
                FailedResource = table.Column<string>(type: "TEXT", nullable: true),
                IsRetryable = table.Column<bool>(type: "INTEGER", nullable: false),
                ErrorOccurredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                FilesFound = table.Column<int>(type: "INTEGER", nullable: false),
                FilesAdded = table.Column<int>(type: "INTEGER", nullable: false),
                FilesSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                FilesErrored = table.Column<int>(type: "INTEGER", nullable: false),
                TotalBytesProcessed = table.Column<long>(type: "INTEGER", nullable: false),
                IncludeSubdirectories = table.Column<bool>(type: "INTEGER", nullable: false),
                MaxFileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                ModifiedAfter = table.Column<DateTime>(type: "TEXT", nullable: true),
                FileExtensions = table.Column<string>(type: "TEXT", nullable: true),
                MinFileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                DuplicateHandling = table.Column<string>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ProcessingStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Parameters = table.Column<string>(type: "TEXT", nullable: false),
                ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                ErrorDetails = table.Column<string>(type: "TEXT", nullable: true),
                FilesFound = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                FilesAdded = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                FilesSkipped = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                FilesErrored = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: true),
                MotionType = table.Column<int>(type: "INTEGER", nullable: true),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                FirmwareVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                WifiRssi = table.Column<int>(type: "INTEGER", nullable: true),
                NfcReaderOk = table.Column<bool>(type: "INTEGER", nullable: false),
                FreeHeap = table.Column<int>(type: "INTEGER", nullable: true),
                LastHeartbeat = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastScanAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastScannedSpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
            name: "PrinterDispatchStates",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                AutoDispatchState = table.Column<int>(type: "INTEGER", nullable: false),
                BedPreConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
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
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                LastHistorySeedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastModelSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastCapabilityUpdate = table.Column<DateTime>(type: "TEXT", nullable: false),
                ObicoServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                TotalPrintHours = table.Column<double>(type: "REAL", nullable: false),
                TotalJobsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                TotalJobsFailed = table.Column<int>(type: "INTEGER", nullable: false),
                TotalFilamentUsedGrams = table.Column<double>(type: "REAL", nullable: false),
                TotalFilamentUsedMeters = table.Column<double>(type: "REAL", nullable: false),
                LastSyncTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagsId = table.Column<Guid>(type: "TEXT", nullable: false)
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
                    name: "FK_PrinterTag_Tag_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tag",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Spools",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
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
            name: "Toolheads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Index = table.Column<int>(type: "INTEGER", nullable: false),
                HotendModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                ExtruderModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                ToolheadModelDefId = table.Column<Guid>(type: "TEXT", nullable: true),
                NozzleModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                SupportedMaterials = table.Column<string>(type: "TEXT", nullable: true),
                IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                ToolheadType = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                CurrentSpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                CurrentMaterial = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CurrentFilamentColor = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagsId = table.Column<Guid>(type: "TEXT", nullable: false)
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
                    name: "FK_GcodeFileTag_Tag_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tag",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: true),
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
                EstimatedCost = table.Column<decimal>(type: "TEXT", nullable: true),
                ActualCost = table.Column<decimal>(type: "TEXT", nullable: true),
                MaterialCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                EnergyCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                MachineTimeCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                LaborCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                TotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                CostCalculatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                PreferredPrinterIds = table.Column<string>(type: "TEXT", nullable: true),
                ExcludedPrinterIds = table.Column<string>(type: "TEXT", nullable: true),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ExternalJobId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                SourcePrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                WasSeededFromHistory = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                IsExternalPrint = table.Column<bool>(type: "INTEGER", nullable: false),
                Copies = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                CompletedCopies = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                ProjectFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                ProjectName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                SpoolmanFilamentId = table.Column<int>(type: "INTEGER", nullable: true),
                SpoolmanSpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                FilamentName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                FilamentVendor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                FilamentColor = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                DispatchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                DispatchScore = table.Column<double>(type: "REAL", nullable: true),
                DispatchMode = table.Column<int>(type: "INTEGER", nullable: true)
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
            name: "HarvestDiscoveredFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                HarvestOperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                FilePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                ThumbnailUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Error = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                DiscoveredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                AlreadyInLibrary = table.Column<bool>(type: "INTEGER", nullable: false),
                FileHash = table.Column<string>(type: "TEXT", nullable: true),
                ExtractedNozzleDiameter = table.Column<double>(type: "REAL", nullable: true),
                ExtractedMaterial = table.Column<string>(type: "TEXT", nullable: true),
                ExtractedPrintTime = table.Column<double>(type: "REAL", nullable: true),
                ExtractedFilamentLength = table.Column<double>(type: "REAL", nullable: true),
                ExtractedSlicerName = table.Column<string>(type: "TEXT", nullable: true),
                ExtractedSlicerVersion = table.Column<string>(type: "TEXT", nullable: true),
                ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                MaintenancePlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                MaintenanceTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                IntervalHoursOverride = table.Column<double>(type: "REAL", nullable: true),
                IntervalDaysOverride = table.Column<int>(type: "INTEGER", nullable: true)
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
            name: "PrinterMaintenanceSchedules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                MaintenancePlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                DeployedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "NfcScanEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                NfcDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                SpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                TagFormat = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                MaterialType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                BrandName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "DispatchLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                DispatchMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Score = table.Column<double>(type: "REAL", nullable: true),
                ScoreBreakdown = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                ScoringDetails = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                DispatchedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OriginalJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                RetryJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                ErrorCategory = table.Column<int>(type: "INTEGER", nullable: false),
                FailureReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                ScheduledRetryTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                ActualRetryTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "Pending"),
                Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                ScheduledStartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                TimeZone = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "UTC"),
                RecurrencePattern = table.Column<string>(type: "TEXT", nullable: true),
                RecurrenceEndDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                IsPaused = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                ScheduledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                FromState = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                ToState = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                TransitionedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                DurationInState = table.Column<long>(type: "INTEGER", nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Body = table.Column<string>(type: "TEXT", nullable: false),
                Metadata = table.Column<string>(type: "TEXT", nullable: true),
                IsRead = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
            name: "PrintApprovals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                RequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
            name: "PrintJobStatistics",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                ActualDurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                EstimatedDurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                Material = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                NozzleTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                BedTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                SpeedPercentage = table.Column<int>(type: "INTEGER", nullable: false),
                IsSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                FailureReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                EstimatedCost = table.Column<decimal>(type: "TEXT", nullable: true),
                ActualCost = table.Column<decimal>(type: "TEXT", nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "PrintJobToolheadUsages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToolheadIndex = table.Column<int>(type: "INTEGER", nullable: false),
                SpoolmanSpoolId = table.Column<int>(type: "INTEGER", nullable: true),
                FilamentUsageGrams = table.Column<double>(type: "REAL", nullable: true),
                SlicerEstimateGrams = table.Column<double>(type: "REAL", nullable: true),
                FilamentName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                FilamentColor = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                MaterialCostUsd = table.Column<decimal>(type: "TEXT", nullable: true)
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                PrintProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                SpoolmanFilamentId = table.Column<int>(type: "INTEGER", nullable: true),
                MaterialRequirement = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                PrintCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                PrintedCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastPrintedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastPrintJobId = table.Column<Guid>(type: "TEXT", nullable: true)
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
            name: "HarvestFileGcodeFileMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                HarvestDiscoveredFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "MaintenanceAlerts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterMaintenanceScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                MaintenanceTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                Title = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Severity = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                PrinterHoursAtTrigger = table.Column<double>(type: "REAL", nullable: false),
                HoursSinceLastMaintenance = table.Column<double>(type: "REAL", nullable: true),
                DaysSinceLastMaintenance = table.Column<int>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                DismissedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                DismissedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                DismissalReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "JobExecutions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true),
                JobScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                ScheduledExecutionTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                ActualStartTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
            name: "MaintenanceLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterMaintenanceScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                ResolvedAlertId = table.Column<Guid>(type: "TEXT", nullable: true),
                MaintenanceTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                TaskName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                Component = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                PerformedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                PerformedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DurationMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                PartsReplaced = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Cost = table.Column<decimal>(type: "TEXT", nullable: true),
                PrinterHoursAtMaintenance = table.Column<double>(type: "REAL", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
                    column: x => x.ResolvedAlertId,
                    principalTable: "MaintenanceAlerts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
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
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.InsertData(
            table: "DispatchSettings",
            columns: new[] { "Id", "AutoDispatchEnabled", "AutoDispatchMode", "CreatedDate", "IdleThresholdSeconds", "LoadBalancingStrategy", "MaxConcurrentDispatches", "MinimumScoreThreshold", "UpdatedAt", "UpdatedDate" },
            values: new object[] { 1, false, "Manual", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 30, "BestFit", 3, 0.5, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

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
            name: "IX_JobExecutions_JobScheduleId_ScheduledExecutionTime",
            table: "JobExecutions",
            columns: new[] { "JobScheduleId", "ScheduledExecutionTime" });

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
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Locations_Path",
            table: "Locations",
            column: "Path");

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
            column: "ResolvedAlertId");

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
            name: "IX_Model3DTag_TagsId",
            table: "Model3DTag",
            column: "TagsId");

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
            name: "IX_PrintApprovals_CreatedAt",
            table: "PrintApprovals",
            column: "CreatedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_PrintApprovals_PrintJobId",
            table: "PrintApprovals",
            column: "PrintJobId");

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
            name: "IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId",
            table: "PrinterMaintenanceSchedules",
            columns: new[] { "MaintenancePlanId", "PrinterId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterMaintenanceSchedules_PrinterId",
            table: "PrinterMaintenanceSchedules",
            column: "PrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelName_SlicerType",
            table: "PrinterModelAliases",
            columns: new[] { "PrinterModelId", "SlicerModelName", "SlicerType" },
            unique: true);

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
            name: "IX_PrintJobs_AssignedPrinterId",
            table: "PrintJobs",
            column: "AssignedPrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_AssignedPrinterId_Status",
            table: "PrintJobs",
            columns: new[] { "AssignedPrinterId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_ExternalJobId_SourcePrinterId",
            table: "PrintJobs",
            columns: new[] { "ExternalJobId", "SourcePrinterId" },
            unique: true);

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
            name: "IX_PrintJobs_SourcePrinterId",
            table: "PrintJobs",
            column: "SourcePrinterId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_Status",
            table: "PrintJobs",
            column: "Status");

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
            name: "IX_PrintJobToolheadUsages_PrintJobId_ToolheadIndex",
            table: "PrintJobToolheadUsages",
            columns: new[] { "PrintJobId", "ToolheadIndex" },
            unique: true);

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
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId",
            table: "PrintProjectFiles",
            columns: new[] { "PrintProjectId", "GcodeFileId" },
            unique: true);

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
            name: "IX_Tag_CreatedAt",
            table: "Tag",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Tag_Name",
            table: "Tag",
            column: "Name",
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
            name: "IX_UserActions_Name",
            table: "UserActions",
            column: "Name",
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
            name: "IX_UserTasks_DismissedByUserId",
            table: "UserTasks",
            column: "DismissedByUserId");

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
            name: "AuthAuditLogs");

        migrationBuilder.DropTable(
            name: "Cameras");

        migrationBuilder.DropTable(
            name: "CatalogVersions");

        migrationBuilder.DropTable(
            name: "DispatchLogs");

        migrationBuilder.DropTable(
            name: "DispatchSettings");

        migrationBuilder.DropTable(
            name: "FailedLoginAttempts");

        migrationBuilder.DropTable(
            name: "FailureDetectionIncidents");

        migrationBuilder.DropTable(
            name: "FilamentTypePrinterModel");

        migrationBuilder.DropTable(
            name: "FileHealthAudits");

        migrationBuilder.DropTable(
            name: "GcodeFileTag");

        migrationBuilder.DropTable(
            name: "GcodeHarvestQueueItems");

        migrationBuilder.DropTable(
            name: "HarvestFileGcodeFileMappings");

        migrationBuilder.DropTable(
            name: "JobExecutions");

        migrationBuilder.DropTable(
            name: "JobRetries");

        migrationBuilder.DropTable(
            name: "JobStateHistories");

        migrationBuilder.DropTable(
            name: "MaintenanceLogs");

        migrationBuilder.DropTable(
            name: "MaintenanceTaskComponents");

        migrationBuilder.DropTable(
            name: "Model3DTag");

        migrationBuilder.DropTable(
            name: "NfcScanEvents");

        migrationBuilder.DropTable(
            name: "NotificationPreferences");

        migrationBuilder.DropTable(
            name: "Notifications");

        migrationBuilder.DropTable(
            name: "PasswordPolicies");

        migrationBuilder.DropTable(
            name: "PasswordResetTokens");

        migrationBuilder.DropTable(
            name: "PlanTasks");

        migrationBuilder.DropTable(
            name: "PrintApprovals");

        migrationBuilder.DropTable(
            name: "PrinterDispatchStates");

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
            name: "PrintJobStatistics");

        migrationBuilder.DropTable(
            name: "PrintJobToolheadUsages");

        migrationBuilder.DropTable(
            name: "PrintProjectFiles");

        migrationBuilder.DropTable(
            name: "PrintProjectTemplateFiles");

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
            name: "Spools");

        migrationBuilder.DropTable(
            name: "SystemLogs");

        migrationBuilder.DropTable(
            name: "Toolheads");

        migrationBuilder.DropTable(
            name: "UserRoles");

        migrationBuilder.DropTable(
            name: "UserTasks");

        migrationBuilder.DropTable(
            name: "WebhookDeliveryLogs");

        migrationBuilder.DropTable(
            name: "FilamentTypes");

        migrationBuilder.DropTable(
            name: "HarvestDiscoveredFiles");

        migrationBuilder.DropTable(
            name: "JobSchedules");

        migrationBuilder.DropTable(
            name: "MaintenanceAlerts");

        migrationBuilder.DropTable(
            name: "MaintenanceComponents");

        migrationBuilder.DropTable(
            name: "NfcDevices");

        migrationBuilder.DropTable(
            name: "ObicoServers");

        migrationBuilder.DropTable(
            name: "Tag");

        migrationBuilder.DropTable(
            name: "PrintProjects");

        migrationBuilder.DropTable(
            name: "PrintProjectTemplates");

        migrationBuilder.DropTable(
            name: "Resources");

        migrationBuilder.DropTable(
            name: "UserActions");

        migrationBuilder.DropTable(
            name: "ToolheadModelDefinitions");

        migrationBuilder.DropTable(
            name: "Roles");

        migrationBuilder.DropTable(
            name: "Users");

        migrationBuilder.DropTable(
            name: "WebhookSubscriptions");

        migrationBuilder.DropTable(
            name: "GcodeHarvestOperations");

        migrationBuilder.DropTable(
            name: "PrintJobs");

        migrationBuilder.DropTable(
            name: "MaintenanceTasks");

        migrationBuilder.DropTable(
            name: "PrinterMaintenanceSchedules");

        migrationBuilder.DropTable(
            name: "ExtruderModelDefinitions");

        migrationBuilder.DropTable(
            name: "HotendModelDefinitions");

        migrationBuilder.DropTable(
            name: "NozzleModelDefinitions");

        migrationBuilder.DropTable(
            name: "GcodeFiles");

        migrationBuilder.DropTable(
            name: "MaintenancePlans");

        migrationBuilder.DropTable(
            name: "FolderNode");

        migrationBuilder.DropTable(
            name: "Printers");

        migrationBuilder.DropTable(
            name: "Locations");

        migrationBuilder.DropTable(
            name: "PrinterGroups");

        migrationBuilder.DropTable(
            name: "PrinterModels");

        migrationBuilder.DropTable(
            name: "Manufacturers");
    }
}
