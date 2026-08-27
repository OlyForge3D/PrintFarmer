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
            // #2080 N-NORM-1 (review finding): the very divergence this migration fixes let
            // case/whitespace-variant duplicates accumulate under the old raw-column unique
            // index, so a production database may already hold rows that violate the new
            // normalized-column uniqueness. Deduplicate first (keeping the row with the
            // smallest Id per group) so CreateIndex below cannot fail on pre-existing data.
            migrationBuilder.Sql(
                """
                DELETE FROM "PrinterModelAliases"
                WHERE "Id" IN (
                    SELECT a."Id"
                    FROM "PrinterModelAliases" a
                    JOIN "PrinterModelAliases" b
                        ON a."PrinterModelId" = b."PrinterModelId"
                       AND a."SlicerModelNameNormalized" = b."SlicerModelNameNormalized"
                       AND (a."SlicerTypeNormalized" = b."SlicerTypeNormalized"
                            OR (a."SlicerTypeNormalized" IS NULL AND b."SlicerTypeNormalized" IS NULL))
                       AND a."Id" > b."Id"
                );
                """);

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
