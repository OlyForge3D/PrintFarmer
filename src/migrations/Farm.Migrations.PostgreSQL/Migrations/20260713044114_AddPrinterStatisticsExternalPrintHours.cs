using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
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
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            // Backfill the external-only baseline for existing rows to their current total hours
            // (issue #711, round-5 FIX 1). This is a best-guess baseline: it may include prior
            // PrintFarmer-job inflation, so the first post-migration sync yields a zero external
            // delta, after which ExternalPrintHours tracks only external growth correctly.
            migrationBuilder.Sql(
                "UPDATE \"PrinterStatisticsSet\" SET \"ExternalPrintHours\" = \"TotalPrintHours\";");
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
