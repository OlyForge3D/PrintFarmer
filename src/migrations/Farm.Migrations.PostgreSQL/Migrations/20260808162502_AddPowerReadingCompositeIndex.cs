using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPowerReadingCompositeIndex : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// PowerReadings is append-only (one row per monitor per ~30s poll) and grows
        /// unboundedly, so a plain CREATE/DROP INDEX would take an ACCESS EXCLUSIVE lock
        /// and block polling inserts for the duration of the index build. CONCURRENTLY
        /// avoids that at the cost of running each statement outside the migration's
        /// wrapping transaction (required by PostgreSQL for concurrent index DDL).
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"IX_PowerReadings_PowerMonitorId\";",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_PowerReadings_PowerMonitorId_RecordedAt\" " +
                "ON \"PowerReadings\" (\"PowerMonitorId\", \"RecordedAt\");",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"IX_PowerReadings_PowerMonitorId_RecordedAt\";",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_PowerReadings_PowerMonitorId\" " +
                "ON \"PowerReadings\" (\"PowerMonitorId\");",
                suppressTransaction: true);
        }
    }
}
