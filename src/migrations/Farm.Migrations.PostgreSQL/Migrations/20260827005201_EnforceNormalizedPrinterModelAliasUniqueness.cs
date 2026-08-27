using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class EnforceNormalizedPrinterModelAliasUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelName_SlicerTy~",
                table: "PrinterModelAliases");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelNameNormalize~",
                table: "PrinterModelAliases",
                columns: new[] { "PrinterModelId", "SlicerModelNameNormalized", "SlicerTypeNormalized" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelNameNormalize~",
                table: "PrinterModelAliases");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterModelAliases_PrinterModelId_SlicerModelName_SlicerTy~",
                table: "PrinterModelAliases",
                columns: new[] { "PrinterModelId", "SlicerModelName", "SlicerType" },
                unique: true);
        }
    }
}
