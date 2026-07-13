using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterStatisticsExternalPrintHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ExternalPrintHours",
                table: "PrinterStatisticsSet",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            // Existing rows intentionally keep the 0 default (issue #711, round-7 Finding 1).
            // Backfilling ExternalPrintHours = TotalPrintHours would snapshot any prior
            // PrintFarmer-job inflation as the "external" baseline and permanently double the PF
            // portion once the reset-then-add pattern runs. Instead the external baseline is left
            // uninitialized (see AddPrinterStatisticsExternalBaselineInitialized) and captured on the
            // first trustworthy external sync.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalPrintHours",
                table: "PrinterStatisticsSet");
        }
    }
}
