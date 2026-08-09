using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Test-infra migration only (this repo's SQLite usage is local dev via EnsureCreated, not
    /// migrations). Backfill uses the same binary/ordinal collation as
    /// <c>EfSliceJobRepository.GetOrdinalEngineNameCollation</c> so the legacy fallback rule
    /// (missing/malformed engine name resolves to OrcaSlicer) matches exactly; SQLite text
    /// columns already default to BINARY collation, so an explicit COLLATE is not required here.
    /// </remarks>
    public partial class AddSliceJobNormalizedEngineStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NormalizedEngine",
                table: "SliceJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE "SliceJobs" SET "NormalizedEngine" = CASE
                    WHEN "SlicerEngineName" = 'PrusaSlicer' THEN 1
                    WHEN "SlicerEngineName" = 'SuperSlicer' THEN 2
                    WHEN "SlicerEngineName" = 'Cura' THEN 3
                    ELSE 0
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_Status_NormalizedEngine",
                table: "SliceJobs",
                columns: new[] { "Status", "NormalizedEngine" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SliceJobs_Status_NormalizedEngine",
                table: "SliceJobs");

            migrationBuilder.DropColumn(
                name: "NormalizedEngine",
                table: "SliceJobs");
        }
    }
}
