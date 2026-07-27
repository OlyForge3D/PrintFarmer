using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class CompleteCalibrationDispatchFencing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LotNumber",
            table: "Spools",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Sku",
            table: "Spools",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "AttemptNumber",
            table: "QueueDispatchOutbox",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AttemptOutcome",
            table: "QueueDispatchOutbox",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "BedClearCommandId",
            table: "QueueDispatchOutbox",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "BedClearExpiresAtUtc",
            table: "QueueDispatchOutbox",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FailureRequiresReconciliation",
            table: "QueueDispatchOutbox",
            type: "bit",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FailureRetryable",
            table: "QueueDispatchOutbox",
            type: "bit",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendFileIdentity",
            table: "QueueDispatchAttempts",
            type: "nvarchar(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "QueueDispatchAttempts",
            type: "rowversion",
            rowVersion: true,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ActiveExternalPrinterId",
            table: "PrintJobs",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PinnedFilamentLotNumber",
            table: "PrintJobs",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PhysicalControlActorSubject",
            table: "PrinterDispatchStates",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PhysicalControlAttemptId",
            table: "PrinterDispatchStates",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PhysicalControlCommandId",
            table: "PrinterDispatchStates",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PhysicalControlOperation",
            table: "PrinterDispatchStates",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "PhysicalControlRequiresReconciliation",
            table: "PrinterDispatchStates",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "PhysicalControlStartedAtUtc",
            table: "PrinterDispatchStates",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "InitiatingActorSubject",
            table: "JobSchedules",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            defaultValue: "system:scheduler");

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "DispatchSettings",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "DispatchSettings",
            type: "rowversion",
            rowVersion: true,
            nullable: true);

        migrationBuilder.UpdateData(
            table: "DispatchSettings",
            keyColumn: "Id",
            keyValue: 1,
            column: "Revision",
            value: 1L);

        migrationBuilder.Sql(
            """
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (
                               PARTITION BY [AssignedPrinterId]
                               ORDER BY COALESCE([ActualStartTime], [QueuedAt]) DESC, [Id]) AS rn
                    FROM [PrintJobs]
                    WHERE [IsExternalPrint] = 1
                      AND [AssignedPrinterId] IS NOT NULL
                      AND [Status] IN (2, 3, 4)
                )
                UPDATE job
                SET [ActiveExternalPrinterId] = job.[AssignedPrinterId]
                FROM [PrintJobs] AS job
                INNER JOIN ranked ON ranked.[Id] = job.[Id]
                WHERE ranked.rn = 1;
                """);

        migrationBuilder.CreateIndex(
            name: "UX_PrintJobs_ActiveExternalPrinterId",
            table: "PrintJobs",
            column: "ActiveExternalPrinterId",
            unique: true,
            filter: "[ActiveExternalPrinterId] IS NOT NULL");
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
