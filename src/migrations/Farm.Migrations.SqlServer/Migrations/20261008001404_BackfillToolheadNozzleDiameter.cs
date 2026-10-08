using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class BackfillToolheadNozzleDiameter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy toolheads stored only a NozzleModelId; the per-printer NozzleDiameter is now the
            // authoritative value, so seed it once from the nozzle model where it was never set.
            migrationBuilder.Sql(
                """
                UPDATE t
                SET t.[NozzleDiameter] = n.[Diameter]
                FROM [Toolheads] AS t
                INNER JOIN [NozzleModelDefinitions] AS n ON t.[NozzleModelId] = n.[Id]
                WHERE t.[NozzleDiameter] IS NULL
                  AND n.[Diameter] > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally irreversible: backfilled values are indistinguishable from values an
            // admin has since saved, so clearing them could destroy per-printer configuration.
        }
    }
}
