using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class BackfillLegacyHarvestedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // "HarvestedAt" shipped as a nullable column in AddPrintedPartsInventory (20260710210946)
        // with no backfill, so every job completed before that point reads as "never harvested"
        // and floods the harvest attention feed. Those plates left the bed before the feature
        // existed and cannot carry part-output mappings, so the harvest action advertised on them
        // can never succeed. Stamp them with their completion time to record the state operators
        // already observed physically. Status 5 is PrintJobStatus.Completed.
        //
        // The cutoff is the feature's fixed ship timestamp, deliberately NOT a deployment-relative
        // "now - 7 days" window. On a delayed upgrade a moving window would also stamp jobs
        // completed after the feature shipped: those are genuinely pending, can hold valid
        // mappings, and stay harvestable from Job History. Stamping them would make
        // POST /api/job-queue/{id}/harvest silently replay as already-harvested and create no
        // inventory, with no way to recover.
        //
        // Timestamps persist as text, so compare the fixed-width second precision prefix against
        // a literal in the same 19-character "yyyy-MM-dd HH:mm:ss" shape.
        migrationBuilder.Sql(
            """
            UPDATE "PrintJobs"
            SET "HarvestedAt" = COALESCE("ActualEndTime", "UpdatedAt")
            WHERE "HarvestedAt" IS NULL
              AND "Status" = 5
              AND substr(COALESCE("ActualEndTime", "UpdatedAt"), 1, 19) < '2026-07-10 21:09:46';
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverting would clear harvest stamps recorded after the upgrade and resurrect the flood.
    }
}
