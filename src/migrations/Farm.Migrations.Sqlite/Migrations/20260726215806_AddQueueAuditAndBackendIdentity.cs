using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddQueueAuditAndBackendIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_QueueDispatchAttempts_PrintJobs_PrintJobId",
            table: "QueueDispatchAttempts");

        migrationBuilder.AddColumn<Guid>(
            name: "AttemptId",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BedClearState",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "DispatchStateRowVersion",
            table: "QueueDispatchOutbox",
            type: "BLOB",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FailureCode",
            table: "QueueDispatchOutbox",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AlterColumn<Guid>(
            name: "PrintJobId",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "TEXT");

        migrationBuilder.AddColumn<string>(
            name: "BackendCommandId",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendFileName",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SliceJobId",
            table: "PrintJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "QueueOperationAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ActorSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ResourceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                PrinterId = table.Column<Guid>(type: "TEXT", nullable: true),
                PrintJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                DispatchAttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                Operation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ReasonCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                JobRowVersion = table.Column<byte[]>(type: "BLOB", nullable: true),
                DispatchStateRowVersion = table.Column<byte[]>(type: "BLOB", nullable: true),
                IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                DetailJson = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueueOperationAudits", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_QueueOperationAudits_OccurredAt",
            table: "QueueOperationAudits",
            column: "OccurredAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_QueueOperationAudits_Printer_OccurredAt",
            table: "QueueOperationAudits",
            columns: new[] { "PrinterId", "OccurredAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_QueueOperationAudits_Resource",
            table: "QueueOperationAudits",
            columns: new[] { "ResourceType", "ResourceId" });

        migrationBuilder.AddForeignKey(
            name: "FK_QueueDispatchAttempts_PrintJobs_PrintJobId",
            table: "QueueDispatchAttempts",
            column: "PrintJobId",
            principalTable: "PrintJobs",
            principalColumn: "Id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_QueueDispatchAttempts_PrintJobs_PrintJobId",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropTable(
            name: "QueueOperationAudits");

        migrationBuilder.DropColumn(
            name: "AttemptId",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "BedClearState",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "DispatchStateRowVersion",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "FailureCode",
            table: "QueueDispatchOutbox");

        migrationBuilder.DropColumn(
            name: "BackendCommandId",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "BackendFileName",
            table: "QueueDispatchAttempts");

        migrationBuilder.DropColumn(
            name: "SliceJobId",
            table: "PrintJobs");

        migrationBuilder.Sql(
            "DELETE FROM \"QueueDispatchAttempts\" WHERE \"PrintJobId\" IS NULL;");

        migrationBuilder.AlterColumn<Guid>(
            name: "PrintJobId",
            table: "QueueDispatchAttempts",
            type: "TEXT",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "TEXT",
            oldNullable: true);

        migrationBuilder.AddForeignKey(
            name: "FK_QueueDispatchAttempts_PrintJobs_PrintJobId",
            table: "QueueDispatchAttempts",
            column: "PrintJobId",
            principalTable: "PrintJobs",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
