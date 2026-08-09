using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// SliceJobs is append-only (grows unboundedly, never pruned) and CREATE INDEX must not take
    /// an ACCESS EXCLUSIVE lock that would block queue inserts/updates for the duration of the
    /// build, so this uses CREATE INDEX CONCURRENTLY (matching AddPowerReadingCompositeIndex).
    /// CONCURRENTLY can't run inside a transaction, so this is a separate migration from the
    /// AddSliceJobNormalizedEngine column+backfill migration: every statement here is idempotent
    /// (IF NOT EXISTS/IF EXISTS), so this migration alone is safe to retry after a failure without
    /// requiring manual DBA cleanup.
    /// </remarks>
    public partial class AddSliceJobNormalizedEngineStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_SliceJobs_Status_NormalizedEngine\" " +
                "ON \"slicer\".\"SliceJobs\" (\"Status\", \"NormalizedEngine\");",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"slicer\".\"IX_SliceJobs_Status_NormalizedEngine\";",
                suppressTransaction: true);
        }
    }
}
