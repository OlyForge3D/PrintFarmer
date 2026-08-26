using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterModelAliasNormalizedLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SlicerModelNameNormalized",
                table: "PrinterModelAliases",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddColumn<string>(
                name: "SlicerTypeNormalized",
                table: "PrinterModelAliases",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "PrinterModelAliases"
                SET "SlicerModelNameNormalized" = UPPER(TRIM("SlicerModelName")),
                    "SlicerTypeNormalized" = CASE
                        WHEN "SlicerType" IS NULL THEN NULL
                        ELSE UPPER(TRIM("SlicerType"))
                    END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "SlicerModelNameNormalized",
                table: "PrinterModelAliases",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256,
                oldDefaultValue: string.Empty);

            migrationBuilder.CreateIndex(
                name: "IX_PrinterModelAliases_NormalizedLookup",
                table: "PrinterModelAliases",
                columns: new[] { "SlicerModelNameNormalized", "SlicerTypeNormalized" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterModelAliases_NormalizedLookup",
                table: "PrinterModelAliases");

            migrationBuilder.DropColumn(
                name: "SlicerModelNameNormalized",
                table: "PrinterModelAliases");

            migrationBuilder.DropColumn(
                name: "SlicerTypeNormalized",
                table: "PrinterModelAliases");
        }
    }
}
