using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class EnforceNormalizedPrinterModelAliasUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelName_SlicerType",
                table: "PrinterModelAliases");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelNameNormalized_SlicerTypeNormalized",
                table: "PrinterModelAliases",
                columns: new[] { "PrinterModelId", "SlicerModelNameNormalized", "SlicerTypeNormalized" },
                unique: true,
                filter: "[SlicerTypeNormalized] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelNameNormalized_SlicerTypeNormalized",
                table: "PrinterModelAliases");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelName_SlicerType",
                table: "PrinterModelAliases",
                columns: new[] { "PrinterModelId", "SlicerModelName", "SlicerType" },
                unique: true,
                filter: "[SlicerType] IS NOT NULL");
        }
    }
}
