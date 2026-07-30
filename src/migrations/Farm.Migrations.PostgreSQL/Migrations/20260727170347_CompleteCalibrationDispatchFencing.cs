using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class CompleteCalibrationDispatchFencing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LotNumber",
            table: "Spools",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Sku",
            table: "Spools",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "AttemptNumber",
            table: "QueueDispatchOutbox",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AttemptOutcome",
            table: "QueueDispatchOutbox",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "BedClearCommandId",
            table: "QueueDispatchOutbox",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "BedClearExpiresAtUtc",
            table: "QueueDispatchOutbox",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FailureRequiresReconciliation",
            table: "QueueDispatchOutbox",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FailureRetryable",
            table: "QueueDispatchOutbox",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendFileIdentity",
            table: "QueueDispatchAttempts",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "QueueDispatchAttempts",
            type: "bytea",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ActiveExternalPrinterId",
            table: "PrintJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PinnedFilamentLotNumber",
            table: "PrintJobs",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PhysicalControlActorSubject",
            table: "PrinterDispatchStates",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PhysicalControlAttemptId",
            table: "PrinterDispatchStates",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PhysicalControlCommandId",
            table: "PrinterDispatchStates",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PhysicalControlOperation",
            table: "PrinterDispatchStates",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "PhysicalControlRequiresReconciliation",
            table: "PrinterDispatchStates",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "PhysicalControlStartedAtUtc",
            table: "PrinterDispatchStates",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "InitiatingActorSubject",
            table: "JobSchedules",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "DispatchSettings",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "DispatchSettings",
            type: "bytea",
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
                SET "RowVersion" = decode(md5("Id"::text), 'hex')
                WHERE "RowVersion" IS NULL;

                UPDATE "DispatchSettings"
                SET "RowVersion" = decode(md5('dispatch-settings-' || "Id"::text), 'hex'),
                    "Revision" = CASE WHEN "Revision" < 1 THEN 1 ELSE "Revision" END
                WHERE "RowVersion" IS NULL;

                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "AssignedPrinterId"
                               ORDER BY COALESCE("ActualStartTime", "QueuedAt") DESC, "Id") AS rn
                    FROM "PrintJobs"
                    WHERE "IsExternalPrint" = TRUE
                      AND "AssignedPrinterId" IS NOT NULL
                      AND "Status" IN (2, 3, 4)
                )
                UPDATE "PrintJobs" AS job
                SET "ActiveExternalPrinterId" = job."AssignedPrinterId"
                FROM ranked
                WHERE job."Id" = ranked."Id" AND ranked.rn = 1;
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
