using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ApiKeys", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AppSettingsEntities",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AppSettingsEntities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Cameras",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                StreamUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                SnapshotUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                Location = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Cameras", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FailedLoginAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                AttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FailureReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FailedLoginAttempts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FilamentTypes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DefaultHotendTemp = table.Column<double>(type: "double precision", nullable: true),
                DefaultBedTemp = table.Column<double>(type: "double precision", nullable: true),
                IsAbrasive = table.Column<bool>(type: "boolean", nullable: false),
                NeedsEnclosure = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentTypes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FileHealthAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AuditDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                AuditType = table.Column<int>(type: "integer", nullable: false),
                FilesChecked = table.Column<int>(type: "integer", nullable: false),
                HealthyFiles = table.Column<int>(type: "integer", nullable: false),
                MissingFiles = table.Column<int>(type: "integer", nullable: false),
                CorruptedFiles = table.Column<int>(type: "integer", nullable: false),
                OrphanedFiles = table.Column<int>(type: "integer", nullable: false),
                MissingFileIds = table.Column<string>(type: "TEXT", nullable: true),
                CorruptedFileIds = table.Column<string>(type: "TEXT", nullable: true),
                OrphanedFilePaths = table.Column<string>(type: "TEXT", nullable: true),
                SummaryMessage = table.Column<string>(type: "TEXT", nullable: true),
                HasIssues = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileHealthAudits", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FolderNode",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                FolderType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FolderNode", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Locations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                PrinterCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Locations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceComponents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                UnitCost = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                Supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                InStock = table.Column<int>(type: "integer", nullable: false),
                MinimumStock = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceComponents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TaskName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                IntervalHours = table.Column<double>(type: "double precision", nullable: true),
                IntervalDays = table.Column<int>(type: "integer", nullable: true),
                EstimatedDurationMinutes = table.Column<int>(type: "integer", nullable: true),
                Priority = table.Column<int>(type: "integer", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                RequiresEnclosure = table.Column<bool>(type: "boolean", nullable: true),
                RequiresCarbonFilter = table.Column<bool>(type: "boolean", nullable: true),
                RequiresHepaFilter = table.Column<bool>(type: "boolean", nullable: true),
                RequiresBowdenTube = table.Column<bool>(type: "boolean", nullable: true),
                RequiresPtfeLiner = table.Column<bool>(type: "boolean", nullable: true),
                RequiresLinearRails = table.Column<bool>(type: "boolean", nullable: true),
                RequiresLeadScrews = table.Column<bool>(type: "boolean", nullable: true),
                RequiresToolchanger = table.Column<bool>(type: "boolean", nullable: true),
                RequiresFilamentCutter = table.Column<bool>(type: "boolean", nullable: true),
                RequiresHeatedChamber = table.Column<bool>(type: "boolean", nullable: true),
                RequiresHeatedBed = table.Column<bool>(type: "boolean", nullable: true),
                RequiresMultiMaterial = table.Column<bool>(type: "boolean", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceTasks", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Manufacturers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Url = table.Column<string>(type: "text", nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                NameLowered = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Manufacturers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PasswordPolicies",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MinLength = table.Column<int>(type: "integer", nullable: false),
                RequireUppercase = table.Column<bool>(type: "boolean", nullable: false),
                RequireLowercase = table.Column<bool>(type: "boolean", nullable: false),
                RequireDigit = table.Column<bool>(type: "boolean", nullable: false),
                RequireSymbol = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PasswordPolicies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrintProjectTemplates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                DefaultPriority = table.Column<int>(type: "integer", nullable: false),
                DefaultNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                IsSystemTemplate = table.Column<bool>(type: "boolean", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjectTemplates", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Resources",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                ResourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Resources", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RetryPolicies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                MaxRetries = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                InitialDelaySeconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                ExponentialBase = table.Column<double>(type: "double precision", nullable: false, defaultValue: 2.0),
                MaxDelaySeconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 3600),
                RetryOnErrorCategories = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Recoverable"),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RetryPolicies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Roles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                IsSystemRole = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Roles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SpoolmanConfigs",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BaseUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SpoolmanConfigs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SystemLogs",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                Exception = table.Column<string>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tag", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UserActions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserActions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                EmailConfirmationToken = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                PasswordResetToken = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                PasswordResetExpires = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastLogin = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FailedLoginAttempts = table.Column<int>(type: "integer", nullable: false),
                LockoutEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastFailedLogin = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceTaskComponents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MaintenanceTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                MaintenanceComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                Notes = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceTaskComponents", x => x.Id);
                table.ForeignKey(
                    name: "FK_MaintenanceTaskComponents_MaintenanceComponents_Maintenance~",
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                GearRatio = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                IsDirectDrive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uuid", nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "text", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MaxTemp = table.Column<int>(type: "integer", nullable: true, defaultValue: 300),
                IsHighFlow = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                MaxFlowRate = table.Column<double>(type: "double precision", nullable: true),
                NozzleInterface = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uuid", nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "text", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Diameter = table.Column<double>(type: "double precision", nullable: false),
                MaxTemp = table.Column<int>(type: "integer", nullable: true, defaultValue: 500),
                NozzleType = table.Column<int>(type: "integer", nullable: false),
                NozzleInterface = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uuid", nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "text", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uuid", nullable: false),
                MotionType = table.Column<int>(type: "integer", nullable: true),
                MaxX = table.Column<double>(type: "double precision", nullable: true),
                MaxY = table.Column<double>(type: "double precision", nullable: true),
                MaxZ = table.Column<double>(type: "double precision", nullable: true),
                DefaultBackend = table.Column<int>(type: "integer", nullable: true),
                HasHeatedBed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                HasEnclosure = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasCarbonFilter = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasHepaFilter = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasBowdenTube = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasPtfeLiner = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasLinearRails = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasLeadScrews = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasToolchanger = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasFilamentCutter = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                HasHeatedChamber = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                MultiMaterial = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                SupportsAutoLeveling = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                MaxBedTemp = table.Column<int>(type: "integer", nullable: true, defaultValue: 120),
                MaxPrintSpeed = table.Column<int>(type: "integer", nullable: true, defaultValue: 150),
                CoverImageUrl = table.Column<string>(type: "text", nullable: true),
                BedTextureUrl = table.Column<string>(type: "text", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                NameLowered = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrintProjectTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                FileNamePattern = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                ColorRequirement = table.Column<int>(type: "integer", nullable: false),
                MaterialRequirement = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                PrintCount = table.Column<int>(type: "integer", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintProjectTemplateFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintProjectTemplateFiles_PrintProjectTemplates_PrintProjec~",
                    column: x => x.PrintProjectTemplateId,
                    principalTable: "PrintProjectTemplates",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Model3DTag",
            columns: table => new
            {
                Model3DId = table.Column<Guid>(type: "uuid", nullable: false),
                TagsId = table.Column<Guid>(type: "uuid", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                Granted = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                EventType = table.Column<int>(type: "integer", nullable: false),
                Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Success = table.Column<bool>(type: "boolean", nullable: false),
                FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Metadata = table.Column<string>(type: "TEXT", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
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
                Id = table.Column<string>(type: "text", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                EnableEmailNotifications = table.Column<bool>(type: "boolean", nullable: false),
                EnablePushNotifications = table.Column<bool>(type: "boolean", nullable: false),
                EnableInAppNotifications = table.Column<bool>(type: "boolean", nullable: false),
                NotifyOnCompletion = table.Column<bool>(type: "boolean", nullable: false),
                NotifyOnFailure = table.Column<bool>(type: "boolean", nullable: false),
                NotifyOnStart = table.Column<bool>(type: "boolean", nullable: false),
                NotifyOnPause = table.Column<bool>(type: "boolean", nullable: false),
                Frequency = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                RetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Token = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IsUsed = table.Column<bool>(type: "boolean", nullable: false),
                UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UsedByIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RevokedByIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                ReplacedByToken = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedByIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TaskType = table.Column<int>(type: "integer", nullable: false),
                EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DismissedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                MetadataJson = table.Column<string>(type: "text", nullable: true),
                RelatedEntityIdsJson = table.Column<string>(type: "text", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
            name: "ToolheadModelDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DefaultHotendId = table.Column<Guid>(type: "uuid", nullable: true),
                DefaultExtruderId = table.Column<Guid>(type: "uuid", nullable: true),
                DefaultNozzleId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ManufacturerId = table.Column<Guid>(type: "uuid", nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Url = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ToolheadModelDefinitions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ToolheadModelDefinitions_ExtruderModelDefinitions_DefaultEx~",
                    column: x => x.DefaultExtruderId,
                    principalTable: "ExtruderModelDefinitions",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_ToolheadModelDefinitions_HotendModelDefinitions_DefaultHote~",
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
                    name: "FK_ToolheadModelDefinitions_NozzleModelDefinitions_DefaultNozz~",
                    column: x => x.DefaultNozzleId,
                    principalTable: "NozzleModelDefinitions",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "FilamentTypePrinterModel",
            columns: table => new
            {
                PrinterModelsId = table.Column<Guid>(type: "uuid", nullable: false),
                SupportedFilamentTypesId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentTypePrinterModel", x => new { x.PrinterModelsId, x.SupportedFilamentTypesId });
                table.ForeignKey(
                    name: "FK_FilamentTypePrinterModel_FilamentTypes_SupportedFilamentTyp~",
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "uuid", nullable: false),
                SlicerModelName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                SlicerType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ServerUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                OriginalServerUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                BackendPort = table.Column<int>(type: "integer", nullable: false),
                FrontendPort = table.Column<int>(type: "integer", nullable: true),
                Notes = table.Column<string>(type: "text", nullable: true),
                Backend = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                ApiKey = table.Column<string>(type: "text", nullable: true),
                Username = table.Column<string>(type: "text", nullable: true),
                Password = table.Column<string>(type: "text", nullable: true),
                CameraStreamUrl = table.Column<string>(type: "text", nullable: true),
                CameraSnapshotUrl = table.Column<string>(type: "text", nullable: true),
                ManufacturerId = table.Column<Guid>(type: "uuid", nullable: false),
                ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                TemplateMachineProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                DateAcquired = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                MaxBuildVolumeX = table.Column<double>(type: "double precision", nullable: true),
                MaxBuildVolumeY = table.Column<double>(type: "double precision", nullable: true),
                MaxBuildVolumeZ = table.Column<double>(type: "double precision", nullable: true),
                HasHeatedBed = table.Column<bool>(type: "boolean", nullable: false),
                HasEnclosure = table.Column<bool>(type: "boolean", nullable: false),
                MultiMaterial = table.Column<bool>(type: "boolean", nullable: false),
                SupportsAutoLeveling = table.Column<bool>(type: "boolean", nullable: false),
                MaxPrintSpeed = table.Column<int>(type: "integer", nullable: true),
                MaxBedTemp = table.Column<int>(type: "integer", nullable: true),
                CurrentMaterial = table.Column<string>(type: "text", nullable: true),
                CurrentSpoolId = table.Column<int>(type: "integer", nullable: true),
                IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                LastCapabilityUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                InMaintenance = table.Column<bool>(type: "boolean", nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                LastHistorySeedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterModelId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Index = table.Column<int>(type: "integer", nullable: false),
                HotendModelId = table.Column<Guid>(type: "uuid", nullable: true),
                ExtruderModelId = table.Column<Guid>(type: "uuid", nullable: true),
                ToolheadModelDefId = table.Column<Guid>(type: "uuid", nullable: true),
                NozzleModelId = table.Column<Guid>(type: "uuid", nullable: true),
                SupportedMaterials = table.Column<string>(type: "text", nullable: true),
                IsPrimary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterModelToolheads", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterModelToolheads_ExtruderModelDefinitions_ExtruderMode~",
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
                    name: "FK_PrinterModelToolheads_ToolheadModelDefinitions_ToolheadMode~",
                    column: x => x.ToolheadModelDefId,
                    principalTable: "ToolheadModelDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "GcodeFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Source = table.Column<int>(type: "integer", nullable: false),
                SourcePrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                OriginalPrinterPath = table.Column<string>(type: "text", nullable: true),
                LastSeenOnPrinter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RequiredNozzleDiameter = table.Column<double>(type: "double precision", nullable: true),
                RequiredMaterial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                EstimatedPrintTimeMinutes = table.Column<double>(type: "double precision", nullable: true),
                EstimatedFilamentLengthMm = table.Column<double>(type: "double precision", nullable: true),
                EstimatedFilamentWeightG = table.Column<double>(type: "double precision", nullable: true),
                ExtractedPrinterModelName = table.Column<string>(type: "text", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "uuid", nullable: true),
                SlicerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                SlicerVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                PrintSettingsId = table.Column<string>(type: "text", nullable: true),
                LayerHeight = table.Column<double>(type: "double precision", nullable: true),
                InfillPercentage = table.Column<double>(type: "double precision", nullable: true),
                Perimeters = table.Column<int>(type: "integer", nullable: true),
                PrintTemperature = table.Column<double>(type: "double precision", nullable: true),
                BedTemperature = table.Column<double>(type: "double precision", nullable: true),
                PrintSpeed = table.Column<double>(type: "double precision", nullable: true),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "text", nullable: false),
                FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                FilePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ThumbnailFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                FileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Description = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastHealthCheckDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                HealthStatus = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                ErrorMessage = table.Column<string>(type: "text", nullable: true),
                ErrorType = table.Column<string>(type: "text", nullable: true),
                ErrorPhase = table.Column<string>(type: "text", nullable: true),
                ErrorDetails = table.Column<string>(type: "text", nullable: true),
                FailedResource = table.Column<string>(type: "text", nullable: true),
                IsRetryable = table.Column<bool>(type: "boolean", nullable: false),
                ErrorOccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                FilesFound = table.Column<int>(type: "integer", nullable: false),
                FilesAdded = table.Column<int>(type: "integer", nullable: false),
                FilesSkipped = table.Column<int>(type: "integer", nullable: false),
                FilesErrored = table.Column<int>(type: "integer", nullable: false),
                TotalBytesProcessed = table.Column<long>(type: "bigint", nullable: false),
                IncludeSubdirectories = table.Column<bool>(type: "boolean", nullable: false),
                MaxFileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                ModifiedAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                FileExtensions = table.Column<string[]>(type: "text[]", nullable: true),
                MinFileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                DuplicateHandling = table.Column<string>(type: "text", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ProcessingStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                Status = table.Column<int>(type: "integer", nullable: false),
                Parameters = table.Column<string>(type: "TEXT", nullable: false),
                ErrorMessage = table.Column<string>(type: "text", nullable: true),
                ErrorDetails = table.Column<string>(type: "TEXT", nullable: true),
                FilesFound = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                FilesAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                FilesSkipped = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                FilesErrored = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "uuid", nullable: true),
                ManufacturerId = table.Column<Guid>(type: "uuid", nullable: true),
                MotionType = table.Column<int>(type: "integer", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                FirmwareVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                WifiRssi = table.Column<int>(type: "integer", nullable: true),
                NfcReaderOk = table.Column<bool>(type: "boolean", nullable: false),
                FreeHeap = table.Column<int>(type: "integer", nullable: true),
                LastHeartbeat = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastScanAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastScannedSpoolId = table.Column<int>(type: "integer", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
            name: "PrinterStatisticsSet",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                TotalPrintHours = table.Column<double>(type: "double precision", nullable: false),
                TotalJobsCompleted = table.Column<int>(type: "integer", nullable: false),
                TotalJobsFailed = table.Column<int>(type: "integer", nullable: false),
                TotalFilamentUsedGrams = table.Column<double>(type: "double precision", nullable: false),
                TotalFilamentUsedMeters = table.Column<double>(type: "double precision", nullable: false),
                LastSyncTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PrinterId1 = table.Column<Guid>(type: "uuid", nullable: true)
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
                table.ForeignKey(
                    name: "FK_PrinterStatisticsSet_Printers_PrinterId1",
                    column: x => x.PrinterId1,
                    principalTable: "Printers",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "Spools",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Material = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                WeightGrams = table.Column<double>(type: "double precision", nullable: false),
                ColorHex = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                InUse = table.Column<bool>(type: "boolean", nullable: false),
                AssignedPrinterId = table.Column<Guid>(type: "uuid", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Index = table.Column<int>(type: "integer", nullable: false),
                HotendModelId = table.Column<Guid>(type: "uuid", nullable: true),
                ExtruderModelId = table.Column<Guid>(type: "uuid", nullable: true),
                ToolheadModelDefId = table.Column<Guid>(type: "uuid", nullable: true),
                NozzleModelId = table.Column<Guid>(type: "uuid", nullable: true),
                SupportedMaterials = table.Column<string>(type: "text", nullable: true),
                IsPrimary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                GcodeFileId = table.Column<Guid>(type: "uuid", nullable: false),
                TagsId = table.Column<Guid>(type: "uuid", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                GcodeFileId = table.Column<Guid>(type: "uuid", nullable: true),
                AssignedPrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                QueuePosition = table.Column<int>(type: "integer", nullable: false),
                RequiredNozzleDiameter = table.Column<decimal>(type: "numeric", nullable: true),
                RequiredMaterialType = table.Column<string>(type: "text", nullable: true),
                RequiredCapabilities = table.Column<string>(type: "text", nullable: true),
                EstimatedPrintTime = table.Column<long>(type: "bigint", nullable: true),
                EstimatedFilamentUsage = table.Column<double>(type: "double precision", nullable: true),
                ActualStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ActualEndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ActualPrintTime = table.Column<long>(type: "bigint", nullable: true),
                ActualFilamentUsage = table.Column<double>(type: "double precision", nullable: true),
                FailureReason = table.Column<string>(type: "text", nullable: true),
                PreferredPrinterIds = table.Column<string>(type: "text", nullable: true),
                ExcludedPrinterIds = table.Column<string>(type: "text", nullable: true),
                Notes = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExternalJobId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                SourcePrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                WasSeededFromHistory = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                ProjectName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                SpoolmanFilamentId = table.Column<int>(type: "integer", nullable: true),
                FilamentName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                FilamentVendor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                FilamentColor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                HarvestOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                FilePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Size = table.Column<long>(type: "bigint", nullable: false),
                ThumbnailUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                Error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                DiscoveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                AlreadyInLibrary = table.Column<bool>(type: "boolean", nullable: false),
                FileHash = table.Column<string>(type: "text", nullable: true),
                ExtractedNozzleDiameter = table.Column<double>(type: "double precision", nullable: true),
                ExtractedMaterial = table.Column<string>(type: "text", nullable: true),
                ExtractedPrintTime = table.Column<double>(type: "double precision", nullable: true),
                ExtractedFilamentLength = table.Column<double>(type: "double precision", nullable: true),
                ExtractedSlicerName = table.Column<string>(type: "text", nullable: true),
                ExtractedSlicerVersion = table.Column<string>(type: "text", nullable: true),
                ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HarvestDiscoveredFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_HarvestDiscoveredFiles_GcodeHarvestOperations_HarvestOperat~",
                    column: x => x.HarvestOperationId,
                    principalTable: "GcodeHarvestOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PlanTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MaintenancePlanId = table.Column<Guid>(type: "uuid", nullable: false),
                MaintenanceTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                IntervalHoursOverride = table.Column<double>(type: "double precision", nullable: true),
                IntervalDaysOverride = table.Column<int>(type: "integer", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MaintenancePlanId = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                DeployedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterMaintenanceSchedules", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterMaintenanceSchedules_MaintenancePlans_MaintenancePla~",
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                NfcDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                SpoolId = table.Column<int>(type: "integer", nullable: true),
                TagFormat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                MaterialType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                BrandName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
            name: "JobRetries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OriginalJobId = table.Column<Guid>(type: "uuid", nullable: false),
                RetryJobId = table.Column<Guid>(type: "uuid", nullable: false),
                AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                ErrorCategory = table.Column<int>(type: "integer", nullable: false),
                FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ScheduledRetryTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ActualRetryTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Pending"),
                Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: true),
                PrintJobId1 = table.Column<Guid>(type: "uuid", nullable: true)
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
                    name: "FK_JobRetries_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_JobRetries_PrintJobs_PrintJobId1",
                    column: x => x.PrintJobId1,
                    principalTable: "PrintJobs",
                    principalColumn: "Id");
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                ScheduledStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                TimeZone = table.Column<string>(type: "text", nullable: false, defaultValue: "UTC"),
                RecurrencePattern = table.Column<string>(type: "text", nullable: true),
                RecurrenceEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                IsPaused = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                FromState = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                ToState = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                TransitionedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DurationInState = table.Column<long>(type: "bigint", nullable: true),
                Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<string>(type: "text", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: true),
                Type = table.Column<int>(type: "integer", nullable: false),
                Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Body = table.Column<string>(type: "TEXT", nullable: false),
                Metadata = table.Column<string>(type: "text", nullable: true),
                IsRead = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                ActualDurationMs = table.Column<long>(type: "bigint", nullable: true),
                EstimatedDurationMs = table.Column<long>(type: "bigint", nullable: true),
                PrinterModelId = table.Column<Guid>(type: "uuid", nullable: true),
                Material = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                NozzleTemperature = table.Column<int>(type: "integer", nullable: true),
                BedTemperature = table.Column<int>(type: "integer", nullable: true),
                SpeedPercentage = table.Column<int>(type: "integer", nullable: false),
                IsSuccess = table.Column<bool>(type: "boolean", nullable: false),
                FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
            name: "PrintProjectFiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                PrintProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "uuid", nullable: false),
                SpoolmanFilamentId = table.Column<int>(type: "integer", nullable: true),
                MaterialRequirement = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                PrintCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                PrintedCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastPrintedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastPrintJobId = table.Column<Guid>(type: "uuid", nullable: true)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                HarvestDiscoveredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                HarvestDiscoveredFileId1 = table.Column<Guid>(type: "uuid", nullable: false),
                GcodeFileId = table.Column<Guid>(type: "uuid", nullable: false),
                GcodeFileId1 = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    name: "FK_HarvestFileGcodeFileMappings_GcodeFiles_GcodeFileId1",
                    column: x => x.GcodeFileId1,
                    principalTable: "GcodeFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_HarvestFileGcodeFileMappings_HarvestDiscoveredFiles_Harvest~",
                    column: x => x.HarvestDiscoveredFileId,
                    principalTable: "HarvestDiscoveredFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_HarvestFileGcodeFileMappings_HarvestDiscoveredFiles_Harves~1",
                    column: x => x.HarvestDiscoveredFileId1,
                    principalTable: "HarvestDiscoveredFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceAlerts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterMaintenanceScheduleId = table.Column<Guid>(type: "uuid", nullable: true),
                MaintenanceTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                Title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Severity = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                PrinterHoursAtTrigger = table.Column<double>(type: "double precision", nullable: false),
                HoursSinceLastMaintenance = table.Column<double>(type: "double precision", nullable: true),
                DaysSinceLastMaintenance = table.Column<int>(type: "integer", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                AcknowledgedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ResolvedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DismissedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                DismissalReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    name: "FK_MaintenanceAlerts_PrinterMaintenanceSchedules_PrinterMainte~",
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                JobScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                ScheduledExecutionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ActualStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterMaintenanceScheduleId = table.Column<Guid>(type: "uuid", nullable: true),
                ResolvedAlertId = table.Column<Guid>(type: "uuid", nullable: true),
                MaintenanceTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                TaskName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Component = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                PerformedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DurationMinutes = table.Column<int>(type: "integer", nullable: true),
                PartsReplaced = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Cost = table.Column<decimal>(type: "numeric", nullable: true),
                PrinterHoursAtMaintenance = table.Column<double>(type: "double precision", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PrinterId1 = table.Column<Guid>(type: "uuid", nullable: true)
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
                    name: "FK_MaintenanceLogs_PrinterMaintenanceSchedules_PrinterMaintena~",
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
                table.ForeignKey(
                    name: "FK_MaintenanceLogs_Printers_PrinterId1",
                    column: x => x.PrinterId1,
                    principalTable: "Printers",
                    principalColumn: "Id");
            });

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
            name: "IX_Cameras_SortOrder",
            table: "Cameras",
            column: "SortOrder");

        migrationBuilder.CreateIndex(
            name: "IX_ExtruderModelDefinitions_ManufacturerId",
            table: "ExtruderModelDefinitions",
            column: "ManufacturerId");

        migrationBuilder.CreateIndex(
            name: "IX_ExtruderModelDefinitions_Name",
            table: "ExtruderModelDefinitions",
            column: "Name");

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
            name: "IX_HarvestFileGcodeFileMappings_GcodeFileId1",
            table: "HarvestFileGcodeFileMappings",
            column: "GcodeFileId1");

        migrationBuilder.CreateIndex(
            name: "IX_HarvestFileGcodeFileMappings_HarvestDiscoveredFileId",
            table: "HarvestFileGcodeFileMappings",
            column: "HarvestDiscoveredFileId");

        migrationBuilder.CreateIndex(
            name: "IX_HarvestFileGcodeFileMappings_HarvestDiscoveredFileId1",
            table: "HarvestFileGcodeFileMappings",
            column: "HarvestDiscoveredFileId1");

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
            name: "IX_JobRetries_PrintJobId",
            table: "JobRetries",
            column: "PrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_JobRetries_PrintJobId1",
            table: "JobRetries",
            column: "PrintJobId1");

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
            name: "IX_Locations_Name",
            table: "Locations",
            column: "Name",
            unique: true);

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
            name: "IX_MaintenanceLogs_PrinterId1",
            table: "MaintenanceLogs",
            column: "PrinterId1");

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
            name: "IX_MaintenanceTaskComponents_MaintenanceTaskId_MaintenanceComp~",
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
            name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelName_SlicerTy~",
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
            name: "IX_Printers_ServerUrl",
            table: "Printers",
            column: "ServerUrl",
            unique: true);

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
            name: "IX_PrinterStatisticsSet_PrinterId1",
            table: "PrinterStatisticsSet",
            column: "PrinterId1",
            unique: true);

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
            name: "FailedLoginAttempts");

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
            name: "PrinterModelAliases");

        migrationBuilder.DropTable(
            name: "PrinterModelToolheads");

        migrationBuilder.DropTable(
            name: "PrinterStatisticsSet");

        migrationBuilder.DropTable(
            name: "PrintJobStatistics");

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
            name: "Tag");

        migrationBuilder.DropTable(
            name: "NfcDevices");

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
            name: "PrinterModels");

        migrationBuilder.DropTable(
            name: "Manufacturers");
    }
}
