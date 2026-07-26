using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddCalibrationQueueDispatch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "BlockedReasonCode",
            table: "PrintJobs",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BlockedReasonJson",
            table: "PrintJobs",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationAttemptId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationConfigSnapshotId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationOrchestrationId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationProjectId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CreatorSubject",
            table: "PrintJobs",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentProfileSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GcodeContentSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IdempotencyKey",
            table: "PrintJobs",
            type: "nvarchar(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IdempotencyRequestSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IdempotencyScope",
            table: "PrintJobs",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "JobKind",
            table: "PrintJobs",
            type: "int",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE [PrintJobs]
            SET [JobKind] = 0
            WHERE [JobKind] IS NULL;
            """);

        migrationBuilder.AddColumn<string>(
            name: "MachineProfileSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "PinnedPrinterConfigRevision",
            table: "PrintJobs",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PrinterConfigSnapshotSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProcessProfileSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RequiredFirmwareFamily",
            table: "PrintJobs",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RequiredGcodeDialect",
            table: "PrintJobs",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RequiredSlicerContainerDigest",
            table: "PrintJobs",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RequiredSlicerDistribution",
            table: "PrintJobs",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RequiredSlicerEngine",
            table: "PrintJobs",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RequiredSlicerVersion",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SourceArtifactId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SpecificationSha256",
            table: "PrintJobs",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "AcknowledgedAtUtc",
            table: "PrinterDispatchStates",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AcknowledgedBySubject",
            table: "PrinterDispatchStates",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "AcknowledgedJobId",
            table: "PrinterDispatchStates",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "AcknowledgementExpiresAtUtc",
            table: "PrinterDispatchStates",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AcknowledgementIdempotencyKey",
            table: "PrinterDispatchStates",
            type: "nvarchar(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ActiveDispatchAttemptId",
            table: "PrinterDispatchStates",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ActiveJobId",
            table: "PrinterDispatchStates",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "QueueDispatchAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "QueueDispatchOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                AggregateType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AggregateRowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PrinterConfigRevision = table.Column<long>(type: "bigint", nullable: true),
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

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_Idempotency_Calibration",
            table: "PrintJobs",
            columns: new[] { "IdempotencyScope", "IdempotencyKey" },
            unique: true,
            filter: "[IdempotencyScope] IS NOT NULL AND [IdempotencyKey] IS NOT NULL AND [JobKind] = 1");

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
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "QueueDispatchAttempts");

        migrationBuilder.DropTable(
            name: "QueueDispatchOutbox");

        migrationBuilder.DropIndex(
            name: "IX_PrintJobs_Idempotency_Calibration",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "BlockedReasonCode",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "BlockedReasonJson",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationAttemptId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationConfigSnapshotId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationOrchestrationId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationProjectId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "CreatorSubject",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "FilamentProfileSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "GcodeContentSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "IdempotencyKey",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "IdempotencyRequestSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "IdempotencyScope",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "JobKind",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "MachineProfileSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedPrinterConfigRevision",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PrinterConfigSnapshotSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "ProcessProfileSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "RequiredFirmwareFamily",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "RequiredGcodeDialect",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "RequiredSlicerContainerDigest",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "RequiredSlicerDistribution",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "RequiredSlicerEngine",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "RequiredSlicerVersion",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "SourceArtifactId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "SpecificationSha256",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "AcknowledgedAtUtc",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "AcknowledgedBySubject",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "AcknowledgedJobId",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "AcknowledgementExpiresAtUtc",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "AcknowledgementIdempotencyKey",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "ActiveDispatchAttemptId",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "ActiveJobId",
            table: "PrinterDispatchStates");
    }
}
