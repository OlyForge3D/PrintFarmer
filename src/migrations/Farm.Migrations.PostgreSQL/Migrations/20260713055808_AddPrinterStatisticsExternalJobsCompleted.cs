using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddPrinterStatisticsExternalJobsCompleted : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ExternalJobsCompleted",
            table: "PrinterStatisticsSet",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        // Existing rows keep the 0 default (issue #711, round-7 Finding 1). See
        // AddPrinterStatisticsExternalPrintHours for why the total is NOT snapshotted as the
        // external baseline.
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ExternalJobsCompleted",
            table: "PrinterStatisticsSet");
    }
}
