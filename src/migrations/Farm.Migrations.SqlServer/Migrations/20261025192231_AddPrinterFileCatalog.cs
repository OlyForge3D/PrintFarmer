using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterFileCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrinterFileIoSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    LeaseOwner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourcePrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseFence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileIoSlots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    SourcePrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisteredPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StorageRoot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IdentityKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ModifiedValue = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProviderVersion = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ProviderDigest = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ObservationRevision = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    MutationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastCompleteScanAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Availability = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterFileLocations_Printers_RegisteredPrinterId",
                        column: x => x.RegisteredPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileScanStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    SourcePrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisteredPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ConfigurationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    MutationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    NextScanAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCompleteScanAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseFence = table.Column<long>(type: "bigint", nullable: false),
                    InteractiveRequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileScanStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterFileScanStates_Printers_RegisteredPrinterId",
                        column: x => x.RegisteredPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandKeyHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SourcePrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationRevision = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    MutationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CommandJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: false),
                    DestinationPrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContentSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Verification = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SnapshotConfirmationSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    SnapshotConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeleteConfirmationSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    DeleteConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrentPathRaceAccepted = table.Column<bool>(type: "bit", nullable: false),
                    DeleteSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancellationRequested = table.Column<bool>(type: "bit", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerLeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WorkerFence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterFileOperations_GcodeFiles_GcodeFileId",
                        column: x => x.GcodeFileId,
                        principalTable: "GcodeFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PrinterFileOperations_PrinterFileLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "PrinterFileLocations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileOrigins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourcePrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GcodeFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StorageRoot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ObservationRevision = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ProviderVersion = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ProviderDigest = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ContentSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Verification = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileOrigins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterFileOrigins_GcodeFiles_GcodeFileId",
                        column: x => x.GcodeFileId,
                        principalTable: "GcodeFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PrinterFileOrigins_PrinterFileLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "PrinterFileLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileRootScans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ScanStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageRoot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Subtree = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IdentityKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ConfigurationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    MutationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LastCompleteScanAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileRootScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterFileRootScans_PrinterFileScanStates_ScanStateId",
                        column: x => x.ScanStateId,
                        principalTable: "PrinterFileScanStates",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileOperationJobs",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileOperationJobs", x => new { x.OperationId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_PrinterFileOperationJobs_PrinterFileOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "PrinterFileOperations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileLocations_RegisteredPrinterId_Availability",
                table: "PrinterFileLocations",
                columns: new[] { "RegisteredPrinterId", "Availability" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileLocations_SourcePrinterId_IdentityKey",
                table: "PrinterFileLocations",
                columns: new[] { "SourcePrinterId", "IdentityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOperationJobs_JobId",
                table: "PrinterFileOperationJobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOperations_ActorId_CommandKeyHash",
                table: "PrinterFileOperations",
                columns: new[] { "ActorId", "CommandKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOperations_GcodeFileId",
                table: "PrinterFileOperations",
                column: "GcodeFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOperations_LocationId",
                table: "PrinterFileOperations",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOperations_State_CreatedAtUtc",
                table: "PrinterFileOperations",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOrigins_GcodeFileId",
                table: "PrinterFileOrigins",
                column: "GcodeFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOrigins_LocationId",
                table: "PrinterFileOrigins",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOrigins_OperationId",
                table: "PrinterFileOrigins",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileOrigins_SourcePrinterId",
                table: "PrinterFileOrigins",
                column: "SourcePrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileRootScans_ScanStateId_IdentityKey",
                table: "PrinterFileRootScans",
                columns: new[] { "ScanStateId", "IdentityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileScanStates_Enabled_NextScanAtUtc",
                table: "PrinterFileScanStates",
                columns: new[] { "Enabled", "NextScanAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileScanStates_RegisteredPrinterId",
                table: "PrinterFileScanStates",
                column: "RegisteredPrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileScanStates_SourcePrinterId",
                table: "PrinterFileScanStates",
                column: "SourcePrinterId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrinterFileIoSlots");

            migrationBuilder.DropTable(
                name: "PrinterFileOperationJobs");

            migrationBuilder.DropTable(
                name: "PrinterFileOrigins");

            migrationBuilder.DropTable(
                name: "PrinterFileRootScans");

            migrationBuilder.DropTable(
                name: "PrinterFileOperations");

            migrationBuilder.DropTable(
                name: "PrinterFileScanStates");

            migrationBuilder.DropTable(
                name: "PrinterFileLocations");
        }
    }
}
