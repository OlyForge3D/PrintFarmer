using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class HardenBedClearReplayStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BedClearCommandRecords_Job_Created_Id",
                table: "BedClearCommandRecords",
                columns: new[] { "JobId", "CreatedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "UX_BedClearCommandRecords_OutboxEventId",
                table: "BedClearCommandRecords",
                column: "OutboxEventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BedClearCommandRecords_Job_Created_Id",
                table: "BedClearCommandRecords");

            migrationBuilder.DropIndex(
                name: "UX_BedClearCommandRecords_OutboxEventId",
                table: "BedClearCommandRecords");
        }
    }
}
