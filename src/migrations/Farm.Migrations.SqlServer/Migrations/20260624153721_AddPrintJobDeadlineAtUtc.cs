using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddPrintJobDeadlineAtUtc : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "DeadlineAtUtc",
            table: "PrintJobs",
            type: "datetime2",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_DeadlineAtUtc",
            table: "PrintJobs",
            column: "DeadlineAtUtc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PrintJobs_DeadlineAtUtc",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "DeadlineAtUtc",
            table: "PrintJobs");
    }
}
