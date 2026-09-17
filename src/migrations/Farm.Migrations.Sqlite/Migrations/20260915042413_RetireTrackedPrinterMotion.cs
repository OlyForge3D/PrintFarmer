using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RetireTrackedPrinterMotion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Retire only idle, attempt-unbound motion barriers. This is not evidence of
            // physical completion: old API/worker processes must be stopped before upgrade.
            migrationBuilder.Sql("""
                UPDATE "PrinterDispatchStates"
                SET "PhysicalControlCommandId" = NULL,
                    "PhysicalControlAttemptId" = NULL,
                    "PhysicalControlOperation" = NULL,
                    "PhysicalControlActorSubject" = NULL,
                    "PhysicalControlStartedAtUtc" = NULL,
                    "PhysicalControlRequiresReconciliation" = 0,
                    "Revision" = "Revision" + 1
                WHERE "PhysicalControlCommandId" IS NOT NULL
                  AND "PhysicalControlOperation" IN ('async_motion', 'home', 'home_xy', 'home_z', 'move', 'move_to')
                  AND "PhysicalControlAttemptId" IS NULL
                  AND "ActiveJobId" IS NULL
                  AND "ActiveDispatchAttemptId" IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "PrintJobs" AS job
                      WHERE job."AssignedPrinterId" = "PrinterDispatchStates"."PrinterId"
                        AND job."Status" IN (2, 3, 4))
                  AND NOT EXISTS (
                      SELECT 1 FROM "QueueDispatchAttempts" AS attempt
                      WHERE attempt."PrinterId" = "PrinterDispatchStates"."PrinterId"
                        AND (attempt."RequiresReconciliation" = 1
                             OR attempt."Outcome" IN (0, 4)
                             OR (attempt."Outcome" = 1 AND attempt."TerminalAtUtc" IS NULL)));
                """);

            // The removed publisher must never retry these invalidations. Preserve their
            // payload and prior errors without pretending they were delivered successfully.
            migrationBuilder.Sql("""
                UPDATE "QueueDispatchOutbox"
                SET "Status" = 3,
                    "CompletedAtUtc" = CURRENT_TIMESTAMP,
                    "RetryAfterUtc" = NULL,
                    "Revision" = "Revision" + 1
                WHERE "EventType" = 'PrintFarmer.Printer.ControlOperationUpdated.v1'
                  AND "Status" IN (0, 1);
                """);

            // These unmapped archives retain uncertain delivery and operator audit evidence.
            migrationBuilder.RenameTable(
                name: "PrinterControlOperations",
                newName: "RetiredPrinterControlOperations");
            migrationBuilder.RenameTable(
                name: "PrinterEmergencyStopAttempts",
                newName: "RetiredPrinterEmergencyStopAttempts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Tracked motion retirement cannot be reversed: restoring queued intents could replay physical commands. Restore a pre-upgrade backup only with all printer writers stopped.");
        }
    }
}
