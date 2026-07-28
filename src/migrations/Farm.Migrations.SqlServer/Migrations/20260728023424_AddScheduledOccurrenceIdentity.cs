using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddScheduledOccurrenceIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "RootPrintJobId",
            table: "JobSchedules",
            type: "uniqueidentifier",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.AddColumn<Guid>(
            name: "DispatchAttemptId",
            table: "JobExecutions",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "OccurrencePrintJobId",
            table: "JobExecutions",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE [JobSchedules]
            SET [RootPrintJobId] = [PrintJobId]
            WHERE [RootPrintJobId] = '00000000-0000-0000-0000-000000000000';

            UPDATE execution
            SET execution.[OccurrencePrintJobId] = schedule.[PrintJobId]
            FROM [JobExecutions] AS execution
            INNER JOIN [JobSchedules] AS schedule
                ON execution.[JobScheduleId] = schedule.[Id]
            WHERE execution.[OccurrencePrintJobId] IS NULL;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_JobSchedules_RootPrintJobId",
            table: "JobSchedules",
            column: "RootPrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_JobExecutions_OccurrencePrintJobId",
            table: "JobExecutions",
            column: "OccurrencePrintJobId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_JobSchedules_RootPrintJobId",
            table: "JobSchedules");

        migrationBuilder.DropIndex(
            name: "IX_JobExecutions_OccurrencePrintJobId",
            table: "JobExecutions");

        migrationBuilder.DropColumn(
            name: "RootPrintJobId",
            table: "JobSchedules");

        migrationBuilder.DropColumn(
            name: "DispatchAttemptId",
            table: "JobExecutions");

        migrationBuilder.DropColumn(
            name: "OccurrencePrintJobId",
            table: "JobExecutions");
    }
}
