using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class CompleteCalibrationDispatchFencing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LotNumber",
            table: "Spools",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Sku",
            table: "Spools",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "AttemptNumber",
            table: "QueueDispatchOutbox",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AttemptOutcome",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "BedClearCommandId",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "BedClearExpiresAtUtc",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FailureRequiresReconciliation",
            table: "QueueDispatchOutbox",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FailureRetryable",
            table: "QueueDispatchOutbox",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendFileIdentity",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "QueueDispatchAttempts",
            type: "BLOB",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ActiveExternalPrinterId",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PinnedFilamentLotNumber",
            table: "PrintJobs",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PhysicalControlActorSubject",
            table: "PrinterDispatchStates",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PhysicalControlAttemptId",
            table: "PrinterDispatchStates",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PhysicalControlCommandId",
            table: "PrinterDispatchStates",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PhysicalControlOperation",
            table: "PrinterDispatchStates",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "PhysicalControlRequiresReconciliation",
            table: "PrinterDispatchStates",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "PhysicalControlStartedAtUtc",
            table: "PrinterDispatchStates",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "InitiatingActorSubject",
            table: "JobSchedules",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "DispatchSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "DispatchSettings",
            type: "BLOB",
            maxLength: 16,
            nullable: true);

        migrationBuilder.UpdateData(
            table: "DispatchSettings",
            keyColumn: "Id",
            keyValue: 1,
            columns: new[] { "Revision", "RowVersion" },
            values: new object[] { 1L, null });

        migrationBuilder.Sql(
            """
                UPDATE "QueueDispatchAttempts"
                SET "RowVersion" = randomblob(16)
                WHERE "RowVersion" IS NULL;

                UPDATE "DispatchSettings"
                SET "RowVersion" = randomblob(16),
                    "Revision" = CASE WHEN "Revision" < 1 THEN 1 ELSE "Revision" END
                WHERE "RowVersion" IS NULL;

                UPDATE "PrintJobs"
                SET "ActiveExternalPrinterId" = "AssignedPrinterId"
                WHERE "Id" IN (
                    SELECT "Id"
                    FROM (
                        SELECT "Id",
                               ROW_NUMBER() OVER (
                                   PARTITION BY "AssignedPrinterId"
                                   ORDER BY COALESCE("ActualStartTime", "QueuedAt") DESC, "Id") AS rn
                        FROM "PrintJobs"
                        WHERE "IsExternalPrint" = 1
                          AND "AssignedPrinterId" IS NOT NULL
                          AND "Status" IN (2, 3, 4)
                    ) AS ranked
                    WHERE ranked.rn = 1
                );
                """);

        migrationBuilder.CreateIndex(
            name: "UX_PrintJobs_ActiveExternalPrinterId",
            table: "PrintJobs",
            column: "ActiveExternalPrinterId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_PrintJobs_ActiveExternalPrinterId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "LotNumber",
            table: "Spools");

        migrationBuilder.DropColumn(
            name: "Sku",
            table: "Spools");

        migrationBuilder.DropColumn(
            name: "AttemptNumber",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "AttemptOutcome",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "BedClearCommandId",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "BedClearExpiresAtUtc",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "FailureRequiresReconciliation",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "FailureRetryable",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "BackendFileIdentity",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "ActiveExternalPrinterId",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PinnedFilamentLotNumber",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PhysicalControlActorSubject",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "PhysicalControlAttemptId",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "PhysicalControlCommandId",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "PhysicalControlOperation",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "PhysicalControlRequiresReconciliation",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "PhysicalControlStartedAtUtc",
            table: "PrinterDispatchStates");

        migrationBuilder.DropColumn(
            name: "InitiatingActorSubject",
            table: "JobSchedules");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "DispatchSettings");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "DispatchSettings");
    }
}
