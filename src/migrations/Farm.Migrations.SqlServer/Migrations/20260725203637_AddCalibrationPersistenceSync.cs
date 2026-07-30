using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddCalibrationPersistenceSync : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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
                CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
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
            name: "IX_PrinterConfigurationSnapshots_AttemptId",
            table: "PrinterConfigurationSnapshots",
            column: "AttemptId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterConfigurationSnapshots_ProjectId_SnapshotSha256",
            table: "PrinterConfigurationSnapshots",
            columns: new[] { "ProjectId", "SnapshotSha256" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
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
            name: "GeneratedProfileRevisionOperations");

        migrationBuilder.DropTable(
            name: "GeneratedProfileRevisions");

        migrationBuilder.DropTable(
            name: "CalibrationAttempts");

        migrationBuilder.DropTable(
            name: "PrinterConfigurationSnapshots");

        migrationBuilder.DropTable(
            name: "CalibrationProjects");
    }
}
