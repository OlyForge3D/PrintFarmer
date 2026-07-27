using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class HardenCalibrationDispatchFencing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "DispatchStateRevision",
            table: "QueueDispatchOutbox",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "JobKind",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "JobRevision",
            table: "QueueDispatchOutbox",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "JobStatus",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ProjectId",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "BackendCallPhase",
            table: "QueueDispatchAttempts",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "BackendCallStartedAtUtc",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendCorrelationId",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "BackendResponseAtUtc",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastReconciledAtUtc",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ReconciliationCount",
            table: "QueueDispatchAttempts",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "TerminalAtUtc",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationManifestSha256",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentSnapshotSha256",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PinnedFilamentSku",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "PinnedGcodeFileSizeBytes",
            table: "PrintJobs",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "PinnedObjectDimensionX",
            table: "PrintJobs",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "PinnedObjectDimensionY",
            table: "PrintJobs",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "PinnedObjectDimensionZ",
            table: "PrintJobs",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PinnedPrinterModelId",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PinnedSpoolId",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PinnedToolheadId",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PinnedToolheadIndex",
            table: "PrintJobs",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "PrintJobs",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "SourceModelSha256",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "AcknowledgedJobRowVersion",
            table: "PrinterDispatchStates",
            type: "BLOB",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "AcknowledgedPrinterConfigRevision",
            table: "PrinterDispatchStates",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "AcknowledgedQueueRevision",
            table: "PrinterDispatchStates",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "QueueRevision",
            table: "PrinterDispatchStates",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "PrinterDispatchStates",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateTable(
            name: "BedClearCommandRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                RequestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ActorSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                JobRowVersion = table.Column<byte[]>(type: "BLOB", nullable: false),
                DispatchStateRowVersion = table.Column<byte[]>(type: "BLOB", nullable: false),
                QueueRevision = table.Column<long>(type: "INTEGER", nullable: false),
                PrinterConfigRevision = table.Column<long>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                OutboxEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                DispatchAttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BedClearCommandRecords", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "QueuePositionStates",
            columns: table => new
            {
                ScopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                NextPosition = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueuePositionStates", x => x.ScopeId);
            });

        migrationBuilder.Sql(
            """
            UPDATE "Printers" SET "RowVersion" = randomblob(16) WHERE "RowVersion" IS NULL;
            UPDATE "PrintJobs" SET "RowVersion" = randomblob(16) WHERE "RowVersion" IS NULL;
            UPDATE "PrinterDispatchStates" SET "RowVersion" = randomblob(16) WHERE "RowVersion" IS NULL;
            UPDATE "QueueDispatchOutbox" SET "RowVersion" = randomblob(16) WHERE "RowVersion" IS NULL;
            UPDATE "OutboxSequenceStates" SET "RowVersion" = randomblob(16) WHERE "RowVersion" IS NULL;
            UPDATE "PrintJobs" SET "Revision" = 1;
            UPDATE "PrinterDispatchStates" SET "Revision" = 1;
            UPDATE "QueueDispatchOutbox"
            SET "JobRevision" = (
                SELECT "Revision" FROM "PrintJobs"
                WHERE "PrintJobs"."Id" = "QueueDispatchOutbox"."AggregateId")
            WHERE EXISTS (
                SELECT 1 FROM "PrintJobs"
                WHERE "PrintJobs"."Id" = "QueueDispatchOutbox"."AggregateId");
            UPDATE "QueueDispatchOutbox"
            SET "DispatchStateRevision" = (
                SELECT "Revision" FROM "PrinterDispatchStates"
                WHERE "PrinterDispatchStates"."PrinterId" = "QueueDispatchOutbox"."PrinterId")
            WHERE "PrinterId" IS NOT NULL
              AND EXISTS (
                SELECT 1 FROM "PrinterDispatchStates"
                WHERE "PrinterDispatchStates"."PrinterId" = "QueueDispatchOutbox"."PrinterId");

            WITH "RankedJobs" AS (
                SELECT "Id",
                       ROW_NUMBER() OVER (
                           PARTITION BY "AssignedPrinterId"
                           ORDER BY "Priority" DESC, "QueuePosition", "QueuedAt", "Id") AS "NewPosition"
                FROM "PrintJobs"
                WHERE "AssignedPrinterId" IS NOT NULL AND "Status" IN (0, 1)
            )
            UPDATE "PrintJobs"
            SET "QueuePosition" = (
                SELECT "NewPosition" FROM "RankedJobs"
                WHERE "RankedJobs"."Id" = "PrintJobs"."Id")
            WHERE "Id" IN (SELECT "Id" FROM "RankedJobs");

            INSERT INTO "QueuePositionStates" ("ScopeId", "NextPosition")
            SELECT COALESCE("AssignedPrinterId", '00000000-0000-0000-0000-000000000000'),
                   MAX("QueuePosition")
            FROM "PrintJobs"
            GROUP BY COALESCE("AssignedPrinterId", '00000000-0000-0000-0000-000000000000');
            """);

        migrationBuilder.CreateIndex(
            name: "UX_PrintJobs_Printer_QueuePosition",
            table: "PrintJobs",
            columns: new[] { "AssignedPrinterId", "QueuePosition" },
            unique: true,
            filter: "\"AssignedPrinterId\" IS NOT NULL AND \"Status\" IN (0, 1)");

        migrationBuilder.CreateIndex(
            name: "IX_BedClearCommandRecords_Status_Expiry",
            table: "BedClearCommandRecords",
            columns: new[] { "Status", "ExpiresAtUtc" });

        migrationBuilder.CreateIndex(
            name: "UX_BedClearCommandRecords_Printer_Key",
            table: "BedClearCommandRecords",
            columns: new[] { "PrinterId", "IdempotencyKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BedClearCommandRecords");

        migrationBuilder.DropTable(
            name: "QueuePositionStates");

        migrationBuilder.DropIndex(
            name: "UX_PrintJobs_Printer_QueuePosition",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "DispatchStateRevision",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "JobKind",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "JobRevision",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "JobStatus",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "BackendCallPhase",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "BackendCallStartedAtUtc",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "BackendCorrelationId",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "BackendResponseAtUtc",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "LastReconciledAtUtc",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "ReconciliationCount",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "TerminalAtUtc",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "CalibrationManifestSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "FilamentSnapshotSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedFilamentSku",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedGcodeFileSizeBytes",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedObjectDimensionX",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedObjectDimensionY",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedObjectDimensionZ",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedPrinterModelId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedSpoolId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedToolheadId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedToolheadIndex",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "SourceModelSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "AcknowledgedJobRowVersion",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "AcknowledgedPrinterConfigRevision",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "AcknowledgedQueueRevision",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "QueueRevision",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "PrinterDispatchStates");
    }
}
