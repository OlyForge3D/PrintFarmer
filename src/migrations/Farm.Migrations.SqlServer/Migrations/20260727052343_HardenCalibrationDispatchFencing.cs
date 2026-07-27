using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class HardenCalibrationDispatchFencing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "DispatchStateRevision",
            table: "QueueDispatchOutbox",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "JobKind",
            table: "QueueDispatchOutbox",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "JobRevision",
            table: "QueueDispatchOutbox",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "JobStatus",
            table: "QueueDispatchOutbox",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ProjectId",
            table: "QueueDispatchOutbox",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "BackendCallPhase",
            table: "QueueDispatchAttempts",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "BackendCallStartedAtUtc",
            table: "QueueDispatchAttempts",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendCorrelationId",
            table: "QueueDispatchAttempts",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "BackendResponseAtUtc",
            table: "QueueDispatchAttempts",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastReconciledAtUtc",
            table: "QueueDispatchAttempts",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ReconciliationCount",
            table: "QueueDispatchAttempts",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "TerminalAtUtc",
            table: "QueueDispatchAttempts",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationManifestSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentSnapshotSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PinnedFilamentSku",
            table: "PrintJobs",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "PinnedGcodeFileSizeBytes",
            table: "PrintJobs",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "PinnedObjectDimensionX",
            table: "PrintJobs",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "PinnedObjectDimensionY",
            table: "PrintJobs",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "PinnedObjectDimensionZ",
            table: "PrintJobs",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PinnedPrinterModelId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PinnedSpoolId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PinnedToolheadId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PinnedToolheadIndex",
            table: "PrintJobs",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "PrintJobs",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "SourceModelSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "AcknowledgedJobRowVersion",
            table: "PrinterDispatchStates",
            type: "varbinary(max)",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "AcknowledgedPrinterConfigRevision",
            table: "PrinterDispatchStates",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "AcknowledgedQueueRevision",
            table: "PrinterDispatchStates",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "QueueRevision",
            table: "PrinterDispatchStates",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "PrinterDispatchStates",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

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

        migrationBuilder.Sql(
            """
            UPDATE [PrintJobs] SET [Revision] = 1;
            UPDATE [PrinterDispatchStates] SET [Revision] = 1;
            UPDATE event
            SET [JobRevision] = job.[Revision]
            FROM [QueueDispatchOutbox] AS event
            INNER JOIN [PrintJobs] AS job ON job.[Id] = event.[AggregateId];
            UPDATE event
            SET [DispatchStateRevision] = state.[Revision]
            FROM [QueueDispatchOutbox] AS event
            INNER JOIN [PrinterDispatchStates] AS state
                ON state.[PrinterId] = event.[PrinterId];

            WITH [RankedJobs] AS (
                SELECT [Id], [QueuePosition],
                       ROW_NUMBER() OVER (
                           PARTITION BY [AssignedPrinterId]
                           ORDER BY [Priority] DESC, [QueuePosition], [QueuedAt], [Id]) AS [NewPosition]
                FROM [PrintJobs]
                WHERE [AssignedPrinterId] IS NOT NULL AND [Status] IN (0, 1)
            )
            UPDATE [RankedJobs] SET [QueuePosition] = [NewPosition];

            INSERT INTO [QueuePositionStates] ([ScopeId], [NextPosition])
            SELECT COALESCE(
                       [AssignedPrinterId],
                       CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier)),
                   MAX([QueuePosition])
            FROM [PrintJobs]
            GROUP BY COALESCE(
                [AssignedPrinterId],
                CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier));
            """);

        migrationBuilder.CreateIndex(
            name: "UX_PrintJobs_Printer_QueuePosition",
            table: "PrintJobs",
            columns: new[] { "AssignedPrinterId", "QueuePosition" },
            unique: true,
            filter: "[AssignedPrinterId] IS NOT NULL AND [Status] IN (0, 1)");

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
