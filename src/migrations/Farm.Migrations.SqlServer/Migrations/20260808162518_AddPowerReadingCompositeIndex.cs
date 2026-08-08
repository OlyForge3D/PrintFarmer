using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// This uses a standard blocking CreateIndex/DropIndex rather than ONLINE=ON. ONLINE=ON
    /// index builds require SQL Server Enterprise/Developer edition and would fail outright
    /// on this repo's default deployment, which runs SQL Server Express
    /// (see scripts/docker/database-templates/sqlserver.yml, MSSQL_PID defaults to Express).
    /// PowerReadings grows at roughly one row per monitor per polling interval, so the
    /// blocking window for this index build is expected to be brief (seconds, not minutes)
    /// even at scale. Operators running Enterprise/Developer who need a zero-downtime build
    /// can apply the index manually with ONLINE=ON before running migrations.
    /// </remarks>
    public partial class AddPowerReadingCompositeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PowerReadings_PowerMonitorId",
                table: "PowerReadings");

            migrationBuilder.CreateIndex(
                name: "IX_PowerReadings_PowerMonitorId_RecordedAt",
                table: "PowerReadings",
                columns: new[] { "PowerMonitorId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PowerReadings_PowerMonitorId_RecordedAt",
                table: "PowerReadings");

            migrationBuilder.CreateIndex(
                name: "IX_PowerReadings_PowerMonitorId",
                table: "PowerReadings",
                column: "PowerMonitorId");
        }
    }
}
