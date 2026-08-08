using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// ONLINE=ON is deliberately not applied to the index build: this repo's default
    /// deployment uses SQL Server Express (see
    /// <c>scripts/docker/database-templates/sqlserver.yml</c>), which doesn't support
    /// ONLINE=ON index builds. This matches the documented precedent in
    /// AddPowerReadingCompositeIndex.
    /// </remarks>
    public partial class AddSliceJobNormalizedEngineStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NormalizedEngine",
                schema: "slicer",
                table: "SliceJobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill uses the same binary/ordinal collation as
            // EfSliceJobRepository.GetOrdinalEngineNameCollation so the legacy fallback rule
            // (missing/malformed engine name resolves to OrcaSlicer) matches exactly.
            migrationBuilder.Sql(
                """
                UPDATE [slicer].[SliceJobs] SET [NormalizedEngine] = CASE
                    WHEN [SlicerEngineName] COLLATE Latin1_General_100_BIN2 = N'PrusaSlicer' THEN 1
                    WHEN [SlicerEngineName] COLLATE Latin1_General_100_BIN2 = N'SuperSlicer' THEN 2
                    WHEN [SlicerEngineName] COLLATE Latin1_General_100_BIN2 = N'Cura' THEN 3
                    ELSE 0
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SliceJobs_Status_NormalizedEngine",
                schema: "slicer",
                table: "SliceJobs",
                columns: new[] { "Status", "NormalizedEngine" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SliceJobs_Status_NormalizedEngine",
                schema: "slicer",
                table: "SliceJobs");

            migrationBuilder.DropColumn(
                name: "NormalizedEngine",
                schema: "slicer",
                table: "SliceJobs");
        }
    }
}
