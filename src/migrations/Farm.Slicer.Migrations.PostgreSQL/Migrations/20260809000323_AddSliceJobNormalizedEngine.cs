using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Column add + backfill only — runs fully inside the default migration transaction (unlike
    /// the follow-up index migration, which needs CREATE INDEX CONCURRENTLY and therefore can't
    /// share a transaction with this one). Keeping them separate means a failure/retry of the
    /// CONCURRENTLY step can never leave this non-idempotent AddColumn needing manual cleanup.
    /// </remarks>
    public partial class AddSliceJobNormalizedEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NormalizedEngine",
                schema: "slicer",
                table: "SliceJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill uses the same binary/ordinal collation as
            // EfSliceJobRepository.GetOrdinalEngineNameCollation so the legacy fallback rule
            // (missing/malformed engine name resolves to OrcaSlicer) matches exactly.
            migrationBuilder.Sql(
                """
                UPDATE "slicer"."SliceJobs" SET "NormalizedEngine" = CASE
                    WHEN "SlicerEngineName" COLLATE "C" = 'PrusaSlicer' THEN 1
                    WHEN "SlicerEngineName" COLLATE "C" = 'SuperSlicer' THEN 2
                    WHEN "SlicerEngineName" COLLATE "C" = 'Cura' THEN 3
                    ELSE 0
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NormalizedEngine",
                schema: "slicer",
                table: "SliceJobs");
        }
    }
}
