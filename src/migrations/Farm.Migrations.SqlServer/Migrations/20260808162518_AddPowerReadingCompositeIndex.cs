using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
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
