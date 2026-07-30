using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class BackfillLegacyHarvestedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // "HarvestedAt" shipped as a nullable column with no backfill, so every job completed
        // before printed-parts inventory reads as "never harvested". Those plates left the bed
        // long ago and carry no part-output mappings, so the harvest action advertised on them
        // can never succeed. Stamp them with their completion time to record the state operators
        // already observed physically. Jobs completed inside the attention window are left
        // untouched so genuinely pending plates stay actionable. Status 5 is PrintJobStatus.Completed.
        migrationBuilder.Sql(
            """
            UPDATE [PrintJobs]
            SET [HarvestedAt] = COALESCE([ActualEndTime], [UpdatedAt])
            WHERE [HarvestedAt] IS NULL
              AND [Status] = 5
              AND COALESCE([ActualEndTime], [UpdatedAt]) < DATEADD(day, -7, GETUTCDATE());
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverting would clear harvest stamps recorded after the upgrade and resurrect the flood.
    }
}
